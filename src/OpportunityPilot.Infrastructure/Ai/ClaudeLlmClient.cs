using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using OpportunityPilot.Application.Ai;
using OpportunityPilot.Domain.Ai;

namespace OpportunityPilot.Infrastructure.Ai;

/// <summary>
/// Claude through the official Anthropic SDK with the person's own key (passed per call, never stored here or logged). Answers are asked for
/// as one JSON object, at low effort: these are short rewriting and extraction jobs, so a quick answer beats long thinking.
/// </summary>
public sealed class ClaudeLlmClient(HttpClient http) : IClaudeClient
{
    public const string KeyRejectedReason = "Claude did not accept your API key. Reconnect it in Settings → Claude.";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private AnthropicClient Client(string apiKey) => new()
    {
        ApiKey = apiKey,
        HttpClient = http,
        MaxRetries = 1,
        Timeout = Timeout,
    };

    public async Task<ClaudeKeyCheck> CheckKeyAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            await Client(apiKey).Models.List(cancellationToken: ct);
            return ClaudeKeyCheck.Ok;
        }
        catch (AnthropicUnauthorizedException) { return ClaudeKeyCheck.Rejected; }
        catch (AnthropicForbiddenException) { return ClaudeKeyCheck.NoPermission; }
        catch (AnthropicRateLimitException) { return ClaudeKeyCheck.RateLimited; }
        catch (Exception e) when (e is AnthropicApiException or AnthropicIOException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ClaudeKeyCheck.Unreachable;
        }
    }

    public async Task<LlmResult> GenerateJsonAsync(string apiKey, string model, string operation, string system, string input, int maxTokens, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await Client(apiKey).Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = Math.Clamp(maxTokens, 512, 16_000),
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                System = system + "\nAnswer with one JSON object only: no prose before or after it, no code fences.",
                Messages = [new UserMessageParam { Content = $"Operation: {operation}\n{input}" }],
            }, ct);

            return Read(response, watch);
        }
        catch (AnthropicUnauthorizedException) { return Fail(AiOutcome.ProviderError, KeyRejectedReason, watch); }
        catch (AnthropicForbiddenException) { return Fail(AiOutcome.ProviderError, "Your Anthropic key may not use this model (403)", watch); }
        catch (AnthropicRateLimitException) { return Fail(AiOutcome.ProviderError, "Claude rate limit reached for your key (429)", watch); }
        catch (AnthropicBadRequestException e) when (e.Message.Contains("credit", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(AiOutcome.ProviderError, "Your Anthropic account has no credit left (console.anthropic.com → Billing)", watch);
        }
        catch (AnthropicBadRequestException) { return Fail(AiOutcome.ProviderError, "Claude rejected the request (400)", watch); }
        catch (Anthropic5xxException) { return Fail(AiOutcome.ProviderError, "Claude is unavailable right now (5xx)", watch); }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Fail(AiOutcome.Timeout, $"Claude timed out after {Timeout.TotalSeconds:0} s", watch);
        }
        catch (Exception e) when (e is AnthropicIOException or HttpRequestException) { return Fail(AiOutcome.ProviderError, "Network error reaching Claude", watch); }
        catch (AnthropicApiException) { return Fail(AiOutcome.ProviderError, "Claude returned an error", watch); }
    }

    private static LlmResult Read(Message response, Stopwatch watch)
    {
        if (response.StopReason == "refusal") return Fail(AiOutcome.InvalidResponse, "Claude declined this request", watch);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return ExtractJson(text) is { } json
            ? new(json, AiOutcome.Succeeded, null, 1, watch.Elapsed, "Claude")
            : Fail(AiOutcome.InvalidResponse, response.StopReason == "max_tokens" ? "Claude's answer was cut off (too long)" : "Claude returned no JSON", watch);
    }

    private static LlmResult Fail(AiOutcome outcome, string reason, Stopwatch watch) => new(null, outcome, reason, 1, watch.Elapsed, "Claude");

    /// <summary>The first JSON object in the text (tolerates a code fence or a stray sentence around it), or null.</summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var candidate = text[start..(end + 1)];
        try { using var _ = System.Text.Json.JsonDocument.Parse(candidate); return candidate; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
