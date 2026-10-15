using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Ai;
using OpportunityPilot.Application.Configuration;
using OpportunityPilot.Domain.Ai;

namespace OpportunityPilot.Infrastructure.Ai;

/// <summary>
/// Groq chat completions (OpenAI-compatible) asking for a JSON object, with the same timeout, single retry on 429/5xx and
/// safe reasons as <see cref="GeminiLlmClient"/>. The key travels in the Authorization header only.
/// </summary>
public sealed class GroqLlmClient(HttpClient http, IOptions<AiOptions> options)
{
    public const int MaxAttempts = 2;
    private readonly AiOptions _options = options.Value;

    public async Task<LlmResult> GenerateJsonAsync(string operation, string system, string input, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string? reason = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 60)));
            using var request = new HttpRequestMessage(HttpMethod.Post, "openai/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.GroqApiKey);
            request.Content = JsonContent.Create(new
            {
                model = _options.GroqModel,
                messages = new object[] { new { role = "system", content = system + " Answer with one JSON object only." }, new { role = "user", content = $"Operation: {operation}\n{input}" } },
                response_format = new { type = "json_object" },
                max_completion_tokens = Math.Clamp(_options.MaxOutputTokens, 128, 4096),
                temperature = 0.2
            });
            try
            {
                using var response = await http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    reason = $"Groq answered {code}";
                    if ((code == 429 || code >= 500) && attempt < MaxAttempts) { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
                    return new(null, AiOutcome.ProviderError, reason, attempt, watch.Elapsed);
                }
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return string.IsNullOrWhiteSpace(text)
                    ? new(null, AiOutcome.InvalidResponse, "Groq returned no text", attempt, watch.Elapsed)
                    : new(text, AiOutcome.Succeeded, null, attempt, watch.Elapsed);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(null, AiOutcome.Timeout, $"Timed out after {Math.Clamp(_options.TimeoutSeconds, 5, 60)} s", attempt, watch.Elapsed);
            }
            catch (HttpRequestException ex)
            {
                reason = ex.StatusCode is { } s ? $"Network error ({(int)s})" : "Network error";
                if (attempt < MaxAttempts) { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
                return new(null, AiOutcome.ProviderError, reason, attempt, watch.Elapsed);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                return new(null, AiOutcome.InvalidResponse, "Groq's answer was not in the expected shape", attempt, watch.Elapsed);
            }
        }
        return new(null, AiOutcome.ProviderError, reason, MaxAttempts, watch.Elapsed);
    }
}

/// <summary>The one AI client the app uses: Groq when its key is set, otherwise Gemini.</summary>
public sealed class RoutingLlmClient(GroqLlmClient groq, GeminiLlmClient gemini, IOptions<AiOptions> options) : ILlmClient
{
    public Task<LlmResult> GenerateJsonAsync(string operation, string system, string input, CancellationToken ct) =>
        options.Value.UsesGroq ? groq.GenerateJsonAsync(operation, system, input, ct) : gemini.GenerateJsonAsync(operation, system, input, ct);
}
