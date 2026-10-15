using System.Net.Http.Headers;
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
public sealed class VoiceController(IHttpClientFactory http, IConfiguration config) : ControllerBase
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private string? GroqKey => config["Groq:ApiKey"] ?? config["Ai:GroqApiKey"];
    private (string? Key, string Url, string Model) Provider => !string.IsNullOrWhiteSpace(GroqKey)
        ? (GroqKey, "https://api.groq.com/openai/v1/audio/transcriptions", config["Groq:WhisperModel"] ?? "whisper-large-v3")
        : (config["OpenAi:Key"], "https://api.openai.com/v1/audio/transcriptions", "whisper-1");

    [HttpGet("status")]
    public ActionResult<VoiceStatusDto> Status() => new VoiceStatusDto(!string.IsNullOrWhiteSpace(Provider.Key), Provider.Model);

    [HttpPost("transcribe")]
    [RequestSizeLimit(MaxBytes + 64 * 1024)]
    public async Task<ActionResult<VoiceTranscriptDto>> Transcribe(IFormFile? audio, CancellationToken ct)
    {
        var (key, url, model) = Provider;
        if (string.IsNullOrWhiteSpace(key))
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Voice transcription is not set up",
                detail: "Set Groq__ApiKey (or OpenAi__Key) on the server, or use your browser's speech input.");
        if (audio is null || audio.Length == 0)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "No audio received", detail: "Record a short voice command and try again.");
        if (audio.Length > MaxBytes)
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Recording too long", detail: "Keep a voice command under about a minute.");

        using var form = new MultipartFormDataContent();
        await using var stream = audio.OpenReadStream();
        var file = new StreamContent(stream);
        file.Headers.ContentType = MediaTypeHeaderValue.TryParse(audio.ContentType, out var type) ? type : new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "file", string.IsNullOrWhiteSpace(audio.FileName) ? "voice.webm" : Path.GetFileName(audio.FileName));
        form.Add(new StringContent(model), "model");

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

public sealed record VoiceStatusDto(bool Whisper, string Model);
public sealed record VoiceTranscriptDto(string Text);
