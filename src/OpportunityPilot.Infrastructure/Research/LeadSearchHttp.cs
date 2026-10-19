using System.Text;
using Microsoft.Extensions.Logging;
using OpportunityPilot.Application.Sales;

namespace OpportunityPilot.Infrastructure.Research;

/// <summary>
/// Posts a lead search to a RapidAPI listing. Only hosts ending in <c>.p.rapidapi.com</c> are called; the key travels only in the
/// request header and is never logged.
/// </summary>
public sealed class LeadSearchHttp(HttpClient http, ILogger<LeadSearchHttp> logger) : ILeadSearchHttp
{
    public async Task<(int Status, string? Body)> PostAsync(string host, string path, string key, string json, CancellationToken ct)
    {
        if (!host.EndsWith(".p.rapidapi.com", StringComparison.OrdinalIgnoreCase) || host.Contains('/')) return (0, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/{path.Trim('/')}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-rapidapi-host", host);
        request.Headers.Add("x-rapidapi-key", key);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) logger.LogWarning("Lead search on {Host}{Path} answered {Status}", host, path, (int)response.StatusCode);
            return ((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Lead search on {Host} failed: {Error}", host, ex.GetType().Name);
            return (0, null);
        }
    }
}
