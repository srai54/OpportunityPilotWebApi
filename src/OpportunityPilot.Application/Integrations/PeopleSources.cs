using System.Text.Json;
using System.Text.RegularExpressions;
using OpportunityPilot.Domain.Integrations;

namespace OpportunityPilot.Application.Integrations;

/// <summary>
/// A person a third-party service (Hunter, Apollo) lists at a company. Third-party data: never verified by this app and never stored.
/// </summary>
/// <param name="Source">The service it came from ("Hunter" or "Apollo").</param>
/// <param name="Email">Hunter: the address it found on public pages. Apollo: only after a reveal (people search never returns emails).</param>
/// <param name="EmailConfidence">Hunter's own 0–100 score for the address; not a verification.</param>
/// <param name="PersonId">Apollo's id, used to reveal the work email (costs the person an Apollo credit).</param>
/// <param name="Note">Where it came from and how sure the service is, in plain words.</param>
public sealed record FoundPersonDto(
    string Source, string Name, string? Title, string? Email, int? EmailConfidence, string? LinkedinUrl, string? PersonId, string Note);

/// <summary>Looks up people at a company with the person's own API key. The key travels only in a header, never in a URL.</summary>
public interface IPeopleSource
{
    IntegrationKind Kind { get; }

    /// <summary>Proves the key against the real account; returns the connection label.</summary>
    Task<string> VerifyAsync(string key, CancellationToken ct);

    Task<IReadOnlyList<FoundPersonDto>> FindAsync(string key, string domain, IReadOnlyList<string> titles, CancellationToken ct);

    /// <summary>Apollo only: the work email of one person. Null when the service has no such person.</summary>
    Task<FoundPersonDto?> RevealAsync(string key, string personId, CancellationToken ct);
}

public static partial class PeopleSearch
{
    public const int MaxPeople = 10;
    public const int MaxTitles = 10;
    public const int MaxTitleLength = 80;

    /// <summary>Who to look for when the request names no titles: people who hire, and people who buy.</summary>
    public static readonly string[] DefaultTitles =
        ["recruiter", "talent acquisition", "HR", "hiring manager", "head of engineering", "CTO", "founder"];

    public const string ThirdParty = "Third-party data, not checked by OpportunityPilot: confirm it before you write.";

    /// <summary>The bare company domain ("acme.com") from a website or a typed domain; null when it is not a public-looking host name.</summary>
    public static string? DomainOf(string? website)
    {
        var text = website?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 300) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.HostNameType != UriHostNameType.Dns) return null;
        var host = u.IdnHost.ToLowerInvariant().TrimEnd('.');
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return HostPattern().IsMatch(host) ? host : null;
    }

    /// <summary>Request titles trimmed, de-duplicated and capped; the defaults when none are given.</summary>
    public static IReadOnlyList<string> Titles(IReadOnlyList<string>? titles)
    {
        var clean = (titles ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return clean.Count == 0 ? DefaultTitles : clean;
    }

    internal static ConnectorProblem Problem(string service, ConnectorResponse r, string? forbidden = null) => new(r.Status switch
    {
        0 => $"{service} could not be reached. Try again in a minute.",
        401 => $"{service} refused the API key. Copy it again from {service} (it may have been deleted or regenerated), then connect again in Connectors.",
        403 when forbidden is not null => $"{forbidden}." + (Detail(r) is { Length: > 0 } d ? $" {service} said{d}".TrimEnd('.') + "." : ""),
        429 => $"{service} says the limit for this key is used up for now (too many requests, or the plan's monthly quota). Try again later or check your {service} plan.",
        _ => $"{service} answered {r.Status}{Detail(r)}".TrimEnd('.') + "."
    });

    /// <summary>The service's own short reason, never the request URL.</summary>
    private static string Detail(ConnectorResponse r)
    {
        var j = r.Json();
        string? text = null;
        if (j.ValueKind == JsonValueKind.Object)
        {
            if (j.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                text = errors[0].Str("details") ?? errors[0].Str("id");
            text ??= j.TryGetProperty("error_details", out var ed) ? ed.Str("message") : null;
            text ??= j.Str("error") ?? j.Str("message");
        }
        return string.IsNullOrWhiteSpace(text) ? "" : ": " + (text.Length > 200 ? text[..200] : text);
    }

    internal static string Name(params string?[] parts) => string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}

/// <summary>
/// Hunter (hunter.io) Domain Search: email addresses Hunter found on public web pages for a domain, with its confidence score and how many
/// pages it saw them on. Free plans return at most 10 per search; each search that finds someone uses one of the plan's monthly searches.
/// </summary>
public sealed class HunterSource(IConnectorHttp http) : IPeopleSource
{
    private const string Api = "https://api.hunter.io/v2";
    public const string Departments = "hr,executive,management,it";
    public IntegrationKind Kind => IntegrationKind.Hunter;

    // Hunter accepts the key as ?api_key=, X-API-KEY or a Bearer header; the header keeps it out of every URL (and so out of logs).
    private static Dictionary<string, string> Key(string key) => new() { ["X-API-KEY"] = key };

    public async Task<string> VerifyAsync(string key, CancellationToken ct)
    {
        var r = await http.SendAsync(new(HttpMethod.Get, $"{Api}/account", Headers: Key(key)), ct);
        if (!r.Ok) throw PeopleSearch.Problem("Hunter", r, "Hunter refused the request");
        var data = r.Json().TryGetProperty("data", out var d) ? d : default;
        var plan = data.Str("plan_name");
        var email = data.Str("email");
        return "Hunter" + (string.IsNullOrWhiteSpace(plan) ? "" : $" {plan} plan") + (string.IsNullOrWhiteSpace(email) ? "" : $" ({email})");
    }

    public async Task<IReadOnlyList<FoundPersonDto>> FindAsync(string key, string domain, IReadOnlyList<string> titles, CancellationToken ct)
    {
        var url = $"{Api}/domain-search?domain={Uri.EscapeDataString(domain)}&limit={PeopleSearch.MaxPeople}&department={Departments}";
        var r = await http.SendAsync(new(HttpMethod.Get, url, Headers: Key(key)), ct);
        if (!r.Ok) throw PeopleSearch.Problem("Hunter", r, "Hunter refused the request");
        return Parse(r.Body, titles);
    }

    /// <summary>People from a Domain Search reply; those whose position matches a wanted title first, then by Hunter's confidence.</summary>
    public static IReadOnlyList<FoundPersonDto> Parse(string body, IReadOnlyList<string> titles)
    {
        var root = new ConnectorResponse(200, body).Json();
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("emails", out var emails) || emails.ValueKind != JsonValueKind.Array) return [];
        var people = new List<(FoundPersonDto Person, bool Wanted)>();
        foreach (var e in emails.EnumerateArray())
        {
            var value = e.Str("value");
            if (string.IsNullOrWhiteSpace(value)) continue;
            var position = e.Str("position");
            var confidence = int.TryParse(e.Str("confidence"), out var c) ? Math.Clamp(c, 0, 100) : (int?)null;
            var pages = e.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
            var generic = e.Str("type") == "generic";
            var name = PeopleSearch.Name(e.Str("first_name"), e.Str("last_name"));
            var status = e.TryGetProperty("verification", out var v) ? v.Str("status") : null;
            var note = $"From Hunter: seen on {pages} public page{(pages == 1 ? "" : "s")}" +
                       (confidence is null ? "" : $", Hunter's confidence {confidence}%") +
                       (string.IsNullOrWhiteSpace(status) ? "" : $", Hunter's own check said \"{status}\"") +
                       (generic ? ". A shared inbox, not a person" : "") + ". " + PeopleSearch.ThirdParty;
            var linkedin = e.Str("linkedin");
            if (linkedin is not null && !linkedin.StartsWith("http", StringComparison.OrdinalIgnoreCase)) linkedin = $"https://www.linkedin.com/in/{linkedin.Trim('/')}";
            var wanted = position is not null && titles.Any(t => position.Contains(t, StringComparison.OrdinalIgnoreCase));
            people.Add((new("Hunter", name.Length > 0 ? name : value, position, value, confidence, linkedin, null, note), wanted));
        }
        return people.OrderByDescending(p => p.Wanted).ThenByDescending(p => p.Person.EmailConfidence ?? 0).Select(p => p.Person).Take(PeopleSearch.MaxPeople).ToList();
    }

    public Task<FoundPersonDto?> RevealAsync(string key, string personId, CancellationToken ct) =>
        throw new ConnectorProblem("Hunter shows the email in the search already; nothing to reveal.");
}

/// <summary>
/// Apollo (apollo.io). People API Search (<c>mixed_people/api_search</c>, no credits) lists people at a domain by title but never returns
/// emails and hides most of the last name; People Enrichment (<c>people/match</c>) reveals one person's work email and uses the person's
/// Apollo credits when it returns data.
/// </summary>
public sealed partial class ApolloSource(IConnectorHttp http) : IPeopleSource
{
    private const string Api = "https://api.apollo.io/api/v1";
    private const string Body = "{}";
    public IntegrationKind Kind => IntegrationKind.Apollo;
    public const string CreditsNote = "Revealing an email uses your Apollo credits (1 credit per email Apollo returns; nothing when it finds nothing).";

    private const string Forbidden = "This Apollo key may not call that endpoint. In Apollo → Settings → Integrations → API, create a key with access to " +
                                     "People API Search and People Enrichment (or a master key). Free Apollo accounts need a work email address";

    private static Dictionary<string, string> Key(string key) => new() { ["x-api-key"] = key };

    [GeneratedRegex("^[A-Za-z0-9]{1,64}$")] private static partial Regex IdPattern();
    public static bool IsPersonId(string? id) => id is not null && IdPattern().IsMatch(id);

    public async Task<string> VerifyAsync(string key, CancellationToken ct)
    {
        var health = await http.SendAsync(new(HttpMethod.Get, $"{Api}/auth/health", Headers: Key(key)), ct);
        if (!health.Ok) throw PeopleSearch.Problem("Apollo", health, Forbidden);
        if (health.Json().TryGetProperty("is_logged_in", out var li) && li.ValueKind == JsonValueKind.False)
            throw PeopleSearch.Problem("Apollo", new ConnectorResponse(401, ""));
        // People search costs no credits; one result proves the key may use the endpoint the finder needs.
        var probe = await http.SendAsync(new(HttpMethod.Post, $"{Api}/mixed_people/api_search?page=1&per_page=1", Body, Headers: Key(key)), ct);
        if (!probe.Ok) throw PeopleSearch.Problem("Apollo", probe, Forbidden);
        return "Apollo account";
    }

    public async Task<IReadOnlyList<FoundPersonDto>> FindAsync(string key, string domain, IReadOnlyList<string> titles, CancellationToken ct)
    {
        var query = $"q_organization_domains_list[]={Uri.EscapeDataString(domain)}" +
                    string.Concat(titles.Select(t => $"&person_titles[]={Uri.EscapeDataString(t)}")) +
                    $"&page=1&per_page={PeopleSearch.MaxPeople}";
        var r = await http.SendAsync(new(HttpMethod.Post, $"{Api}/mixed_people/api_search?{query}", Body, Headers: Key(key)), ct);
        if (!r.Ok) throw PeopleSearch.Problem("Apollo", r, Forbidden);
        return Parse(r.Body);
    }

    public static IReadOnlyList<FoundPersonDto> Parse(string body)
    {
        var root = new ConnectorResponse(200, body).Json();
        if (!root.TryGetProperty("people", out var people) || people.ValueKind != JsonValueKind.Array) return [];
        var list = new List<FoundPersonDto>();
        foreach (var p in people.EnumerateArray())
        {
            var id = p.Str("id");
            if (!IsPersonId(id)) continue;
            var name = PeopleSearch.Name(p.Str("first_name"), p.Str("last_name") ?? p.Str("last_name_obfuscated"));
            var hasEmail = p.TryGetProperty("has_email", out var he) && he.ValueKind == JsonValueKind.True;
            var note = "From Apollo's people search (the last name stays hidden until you reveal). " +
                       (hasEmail ? "Apollo has an email for this person: revealing it uses an Apollo credit. " : "Apollo lists no email for this person. ") +
                       PeopleSearch.ThirdParty;
            list.Add(new("Apollo", name.Length > 0 ? name : "Unnamed person", p.Str("title"), null, null, p.Str("linkedin_url"), id, note));
        }
        return list.Take(PeopleSearch.MaxPeople).ToList();
    }

    public async Task<FoundPersonDto?> RevealAsync(string key, string personId, CancellationToken ct)
    {
        if (!IsPersonId(personId)) throw new ConnectorProblem("That is not an Apollo person id.");
        var r = await http.SendAsync(new(HttpMethod.Post, $"{Api}/people/match?id={personId}&reveal_personal_emails=false&reveal_phone_number=false", Body,
            Headers: Key(key)), ct);
        if (!r.Ok) throw PeopleSearch.Problem("Apollo", r, Forbidden);
        return ParseMatch(r.Body);
    }

    public static FoundPersonDto? ParseMatch(string body)
    {
        var root = new ConnectorResponse(200, body).Json();
        if (!root.TryGetProperty("person", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var email = p.Str("email");
        if (email is not null && (!email.Contains('@') || email.StartsWith("email_not_unlocked", StringComparison.OrdinalIgnoreCase))) email = null;
        var status = p.Str("email_status");
        var name = p.Str("name") ?? PeopleSearch.Name(p.Str("first_name"), p.Str("last_name"));
        var note = email is null
            ? "Apollo returned no work email for this person. " + PeopleSearch.ThirdParty
            : "Work email revealed by Apollo" + (string.IsNullOrWhiteSpace(status) ? "" : $" (Apollo's own status: \"{status}\")") + ". " + PeopleSearch.ThirdParty;
        return new("Apollo", string.IsNullOrWhiteSpace(name) ? "Unnamed person" : name, p.Str("title"), email, null, p.Str("linkedin_url"), p.Str("id"), note);
    }
}
