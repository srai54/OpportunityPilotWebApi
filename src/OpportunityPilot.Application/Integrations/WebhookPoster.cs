using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.Application.Integrations;

/// <summary>A failure the other service reported, in words the person can act on.</summary>
public sealed class ConnectorProblem(string message) : Exception(message);

/// <summary>A short message for a chat channel or an automation tool.</summary>
public sealed record ChannelMessage(string Event, string Title, IReadOnlyList<string> Lines, string? Link = null);

/// <summary>
/// Posts to Slack, Microsoft Teams (Workflows or the older connectors) or any webhook (Zapier, Make, n8n). Only https on the
/// standard port; Slack and Teams addresses must be on their own domains so a mistyped link fails here instead of posting elsewhere.
/// </summary>
public static class WebhookPoster
{
    private static readonly string[] TeamsHosts = [".webhook.office.com", ".logic.azure.com", ".powerplatform.com", ".powerautomate.com"];

    /// <returns>The reason the address is refused, or null when it can be used.</returns>
    public static string? CheckUrl(IntegrationKind kind, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "Paste the webhook address.";
        if (url.Length > 1_000) return "That address is too long.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return "Use the full address starting with https://.";
        if (!u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo)) return "Use the address exactly as the service gave it (https, no port, no login in it).";
        var host = u.IdnHost.ToLowerInvariant();
        return kind switch
        {
            IntegrationKind.Slack when host != "hooks.slack.com" => "A Slack webhook starts with https://hooks.slack.com/. In Slack: Apps → Incoming Webhooks → Add to Slack, pick the channel, copy the Webhook URL.",
            IntegrationKind.Teams when !TeamsHosts.Any(h => host.EndsWith(h, StringComparison.Ordinal)) =>
                "That is not a Teams address. In Teams: the channel's ••• → Workflows → \"Post to a channel when a webhook request is received\", then copy the URL it shows.",
            _ => null
        };
    }

    public static string Body(IntegrationKind kind, ChannelMessage m, DateTime utcNow) => kind switch
    {
        IntegrationKind.Slack => ConnectorJson.Write(new { text = $"*{Escape(m.Title)}*\n" + string.Join("\n", m.Lines.Select(l => "• " + Escape(l))) + (m.Link is null ? "" : $"\n<{m.Link}|Open OpportunityPilot>") }),
        IntegrationKind.Teams => ConnectorJson.Write(new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new Dictionary<string, object>
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new object[] { new { type = "TextBlock", text = m.Title, weight = "Bolder", wrap = true } }
                            .Concat(m.Lines.Select(l => (object)new { type = "TextBlock", text = "• " + l, wrap = true, spacing = "Small" })).ToArray(),
                        ["actions"] = m.Link is null ? Array.Empty<object>() : new object[] { new { type = "Action.OpenUrl", title = "Open OpportunityPilot", url = m.Link } }
                    }
                }
            }
        }),
        _ => ConnectorJson.Write(new { app = "OpportunityPilot", @event = m.Event, title = m.Title, lines = m.Lines, link = m.Link, at = utcNow })
    };

    public static async Task PostAsync(IConnectorHttp http, IntegrationKind kind, string url, ChannelMessage m, DateTime utcNow, CancellationToken ct)
    {
        if (kind == IntegrationKind.Slack && PluginRef.TryParse(url, out _, out var channel))
        {
            // Connected as a plugin: a direct message to the person who connected, through the platform.
            var text = System.Text.Json.Nodes.JsonNode.Parse(Body(kind, m, utcNow))!["text"]!.GetValue<string>();
            var sent = await http.SendAsync(new ConnectorRequest(HttpMethod.Post, "https://slack.com/api/chat.postMessage",
                ConnectorJson.Write(new { channel, text }), Bearer: url), ct);
            var j = sent.Json();
            if (sent.Ok && j.ValueKind == System.Text.Json.JsonValueKind.Object && j.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True) return;
            throw new ConnectorProblem(sent.Status switch
            {
                0 => "Slack could not be reached. It will be tried again next time.",
                401 or 403 => "Slack signed this connection out. Connect Slack again.",
                429 => "Slack is limiting messages right now. It will be tried again next time.",
                _ => $"Slack did not accept the message{(j.Str("error") is { Length: <= 60 } e ? $" ({e})" : "")}."
            });
        }
        var reply = await http.SendAsync(new ConnectorRequest(HttpMethod.Post, url, Body(kind, m, utcNow)), ct);
        if (reply.Ok) return;
        throw new ConnectorProblem(reply.Status switch
        {
            0 when reply.Body == ConnectorResponse.Blocked => "That address is on a private network, not the public internet, so it cannot be used.",
            0 => $"{IntegrationKinds.Name(kind)} could not be reached. Check the address, or try again in a minute.",
            401 or 403 or 404 or 410 => $"{IntegrationKinds.Name(kind)} refused the address (it was removed or the channel was archived). Make a new webhook and connect again.",
            429 => $"{IntegrationKinds.Name(kind)} is limiting messages right now. It will be tried again next time.",
            _ => $"{IntegrationKinds.Name(kind)} answered {reply.Status}. Check the webhook still exists."
        });
    }

    /// <summary>Slack reads &lt;, &gt; and &amp; as markup; text from a posting must not become a link or a mention.</summary>
    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
