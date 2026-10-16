using Microsoft.AspNetCore.Authorization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace OpportunityPilot.Api.Controllers;

/// <summary>
/// Optional Whisper transcription for voice commands, used only when the browser has no speech recognition (for example
/// Firefox). Groq's Whisper (<c>Groq__ApiKey</c>, free tier) is used when set, else OpenAI (<c>OpenAi__Key</c>); the key never
/// reaches the browser. The audio is sent once for transcription and is not stored here.
/// </summary>
[ApiController]
[Route("api/v1/voice")]
public sealed class VoiceController(IHttpClientFactory http, IConfiguration config, OpportunityPilot.Application.Common.ProviderBudget budget) : ControllerBase
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private string? GroqKey => config["Groq:ApiKey"] ?? config["Ai:GroqApiKey"];
    private (string? Key, string Url, string Model) Provider => !string.IsNullOrWhiteSpace(GroqKey)
        ? (GroqKey, "https://api.groq.com/openai/v1/audio/transcriptions", config["Groq:WhisperModel"] ?? "whisper-large-v3")
        : (config["OpenAi:Key"], "https://api.openai.com/v1/audio/transcriptions", "whisper-1");

    [HttpGet("status")]
    public ActionResult<VoiceStatusDto> Status() => new VoiceStatusDto(!string.IsNullOrWhiteSpace(Provider.Key), Provider.Model, !string.IsNullOrWhiteSpace(GroqKey));

    /// <summary>
    /// A natural (human-sounding) English voice for the assistant's reply: Groq's Orpheus text-to-speech, returned as WAV. 503 without
    /// a Groq key, 502 when Groq refuses (for example the model terms were not accepted yet); the browser then uses its own voice.
    /// </summary>
    [Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(OpportunityPilot.Api.Hosting.RateLimits.VoiceSpeak)]
    [HttpPost("speak")]
    public async Task<IActionResult> Speak(SpeakRequest request, CancellationToken ct)
    {
        var key = GroqKey;
        if (string.IsNullOrWhiteSpace(key))
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Natural voice is not set up", detail: "Set Groq__ApiKey on the server.");
        var text = request?.Text?.Trim() ?? "";
        if (text.Length == 0) return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Nothing to say");
        // Orpheus takes at most 200 characters; the browser sends one sentence at a time.
        if (text.Length > 200) text = text[..200];
        if (!budget.TryUse(OpportunityPilot.Application.Common.ProviderBudget.Groq))
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Voice budget used up today", detail: OpportunityPilot.Application.Common.ProviderBudget.ExhaustedMessage);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/speech")
        {
            Content = JsonContent.Create(new
            {
                model = config["Groq:TtsModel"] ?? "canopylabs/orpheus-v1-english",
                voice = config["Groq:TtsVoice"] ?? "autumn",
                input = text,
                response_format = "wav",
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var client = http.CreateClient(nameof(VoiceController));
        client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Groq's own reason (for example "accept the model terms") is safe to show and says what to do; it never contains the key.
            var reason = "";
            try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); reason = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? ""; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "Natural voice unavailable",
                detail: $"The speech service answered {(int)response.StatusCode}. {(reason.Length > 300 ? reason[..300] : reason)}".Trim());
        }
        var audio = await response.Content.ReadAsByteArrayAsync(ct);
        return File(audio, response.Content.Headers.ContentType?.MediaType ?? "audio/wav");
    }

    [Authorize(Policy = OpportunityPilot.Api.Auth.Policies.NotGuest)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(OpportunityPilot.Api.Hosting.RateLimits.VoiceTranscribe)]
    [HttpPost("transcribe")]
    [RequestSizeLimit(MaxBytes + 64 * 1024)]
    public async Task<ActionResult<VoiceTranscriptDto>> Transcribe(IFormFile? audio, [FromForm] string? language, CancellationToken ct)
    {
        var (key, url, model) = Provider;
        if (string.IsNullOrWhiteSpace(key))
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Voice transcription is not set up",
                detail: "Set Groq__ApiKey (or OpenAi__Key) on the server, or use your browser's speech input.");
        if (audio is null || audio.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "No audio received", detail: "Record a short voice command and try again.");
        if (audio.Length > MaxBytes)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Recording too long", detail: "Keep a voice command under about a minute.");

        if (!string.IsNullOrWhiteSpace(GroqKey) && !budget.TryUse(OpportunityPilot.Application.Common.ProviderBudget.Groq))
            return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Voice budget used up today", detail: OpportunityPilot.Application.Common.ProviderBudget.ExhaustedMessage);
        using var form = new MultipartFormDataContent();
        await using var stream = audio.OpenReadStream();
        var file = new StreamContent(stream);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(audio.ContentType, out var type) ? type : new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "file", string.IsNullOrWhiteSpace(audio.FileName) ? "voice.webm" : Path.GetFileName(audio.FileName));
        form.Add(new StringContent(model), "model");
        // Whisper guesses the language per clip and short clips are often guessed wrong; the person's chosen language is sent
        // instead (English or Hindi), and a short list of app words keeps names and terms spelt right.
        var lang = language is "hi" or "en" ? language : "en";
        form.Add(new StringContent(lang), "language");
        form.Add(new StringContent("OpportunityPilot, campaign, jobs, leads, outreach, interview, Gurugram, Bengaluru, Pune, Noida, apply, approve, yes, no"), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var client = http.CreateClient(nameof(VoiceController));
        client.Timeout = TimeSpan.FromSeconds(60);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "Transcription failed",
                detail: $"The speech service answered {(int)response.StatusCode}. Type the command instead.");

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var text = json.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        return new VoiceTranscriptDto(text.Trim());
    }
}

public sealed record VoiceStatusDto(bool Whisper, string Model, bool NaturalVoice = false);
public sealed record SpeakRequest(string Text);
public sealed record VoiceTranscriptDto(string Text);
