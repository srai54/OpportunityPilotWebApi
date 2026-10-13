using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Application.Opportunities;

/// <param name="Kind">Email or LinkedIn.</param>
/// <param name="Label">What it is in plain words: a role inbox, a personal address published on a page, a company page.</param>
/// <param name="MatchesCompanySite">The address is on the company's own domain (the strongest sign it belongs to them).</param>
public sealed record ContactCandidateDto(string Kind, string Value, string Label, string SourceUrl, string SourceNote, bool MatchesCompanySite, int Rank);

public sealed record FindContactsRequest(string? Website);

public sealed record FindContactsResult(IReadOnlyList<ContactCandidateDto> Candidates, IReadOnlyList<string> PagesChecked, string Note);

/// <summary>
/// Pure extraction of contact details that a page or posting already publishes. Nothing is guessed or constructed (no "first.last@" patterns):
/// an address appears only if it is written in the text. Obfuscated addresses ("name [at] site") are left alone.
/// </summary>
public static partial class ContactExtraction
{
    public const int MaxCandidates = 8;

    private static readonly HashSet<string> RoleInboxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "careers", "jobs", "hr", "recruiting", "recruitment", "talent", "people", "hiring", "sales", "hello", "info", "contact", "enquiries",
        "enquiry", "partnerships", "partners", "business", "office", "team", "support",
    };

    private static readonly string[] BlockedDomains = ["example.com", "example.org", "sentry.io", "wixpress.com", "sentry-next.wixpress.com", "domain.com", "email.com"];
    private static readonly string[] ImageSuffixes = [".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".css", ".js"];

    public static bool IsRoleInbox(string email) => RoleInboxes.Contains(email.Split('@')[0]);

    /// <summary>The host with a leading "www." removed; null for anything that is not a plain http(s) URL.</summary>
    public static string? HostOf(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? Normalize(u.Host) : null;

    private static string Normalize(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();

    private static bool OnCompanyDomain(string emailDomain, string? companyHost) =>
        companyHost is not null && (emailDomain.Equals(companyHost, StringComparison.OrdinalIgnoreCase) || emailDomain.EndsWith("." + companyHost, StringComparison.OrdinalIgnoreCase));

    /// <summary>Candidates found in <paramref name="text"/> (page HTML or posting text), ranked best first and de-duplicated.</summary>
    public static List<ContactCandidateDto> Extract(string? text, string sourceUrl, string sourceNote, string? companyHost, bool jobSearch)
    {
        var found = new Dictionary<string, ContactCandidateDto>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in EmailPattern().Matches(text ?? string.Empty))
        {
            var email = m.Value.TrimEnd('.', ',', ';', ':').ToLowerInvariant();
            var domain = email[(email.IndexOf('@') + 1)..];
            if (ImageSuffixes.Any(s => domain.EndsWith(s, StringComparison.Ordinal)) || BlockedDomains.Any(b => domain.Equals(b, StringComparison.Ordinal))) continue;
            if (email.Length > 254 || found.ContainsKey(email)) continue;
            var role = IsRoleInbox(email);
            var onSite = OnCompanyDomain(domain, companyHost);
            var jobBox = role && new[] { "careers", "jobs", "hr", "recruiting", "recruitment", "talent", "people", "hiring" }.Contains(email.Split('@')[0]);
            var rank = (onSite ? 3 : 0) + (role ? 2 : 0) + (role && jobBox == jobSearch ? 1 : 0);
            var label = role ? "Role inbox" : "Personal address published on the page (use it only for the purpose it was published for)";
            found[email] = new("Email", email, label, sourceUrl, sourceNote, onSite, rank);
        }
        foreach (Match m in LinkedInCompany().Matches(text ?? string.Empty))
        {
            var url = m.Value.TrimEnd('/', '.', ',', ';', '"', '\'', ')');
            if (found.ContainsKey(url)) continue;
            found[url] = new("LinkedIn", url, "Company page (find the right person there yourself)", sourceUrl, sourceNote, false, 1);
        }
        return found.Values.OrderByDescending(c => c.Rank).ThenBy(c => c.Value, StringComparer.Ordinal).Take(MaxCandidates).ToList();
    }

    /// <summary>Merges lists from several sources, keeping the best-ranked copy of each value, best first.</summary>
    public static List<ContactCandidateDto> Merge(IEnumerable<IEnumerable<ContactCandidateDto>> lists) =>
        lists.SelectMany(l => l).GroupBy(c => c.Value, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(c => c.Rank).First())
            .OrderByDescending(c => c.Rank).ThenBy(c => c.Value, StringComparer.Ordinal).Take(MaxCandidates).ToList();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,24}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"https?://(?:[a-z]{2,3}\.)?linkedin\.com/company/[A-Za-z0-9\-_%]+/?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkedInCompany();
}

/// <summary>
/// "Find a contact" for an opportunity. It reads only text the person's own research already stored (the posting and its evidence), and at
/// most three public pages of the company's own website (contact, contact-us, about) through the same safe, size-bounded fetcher as research.
/// Results are suggestions with their source; nothing is stored or used until the person picks one.
/// </summary>
public sealed class ContactFinder(IAppDbContext db, ICurrentUser user, IWebFetcher fetcher)
{
    public const int MaxPages = 3;
    private static readonly string[] Paths = ["/contact", "/contact-us", "/about"];

    public async Task<FindContactsResult> FindAsync(Guid opportunityId, FindContactsRequest? request, CancellationToken ct)
    {
        var owner = user.OwnerId;
        var opp = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId && o.OwnerId == owner, ct)
            ?? throw new NotFoundException("Opportunity not found.");
        var job = opp.Mode == OpportunityMode.Job;

        var website = request?.Website?.Trim();
        if (!string.IsNullOrEmpty(website))
        {
            var problem = website.Length > 300 ? "Website is too long." : fetcher.CheckUrl(website);
            if (problem is not null)
                throw new RequestValidationException(new Dictionary<string, string[]> { ["website"] = [problem] });
        }
        // A company lead's own address is its website; for a job the posting URL is the board, so only a website the person gives is used.
        website ??= job ? null : opp.Url;
        var host = ContactExtraction.HostOf(website);

        var lists = new List<List<ContactCandidateDto>>();
        var posting = new List<string?> { opp.Description };
        posting.AddRange(await (from oe in db.OpportunityEvidence join e in db.Evidence on oe.EvidenceId equals e.Id
                                where oe.OpportunityId == opp.Id && e.OwnerId == owner select e.Excerpt).ToListAsync(ct));
        lists.Add(ContactExtraction.Extract(string.Join("\n", posting), opp.Url ?? string.Empty, "Written in the posting or the saved evidence", host, job));

        var checkedPages = new List<string>();
        var note = host is null
            ? "Only the saved posting was read. Add the company's website to also read its contact page."
            : $"Read the saved posting and up to {MaxPages} public pages of {host}.";
        if (host is not null && Uri.TryCreate(website, UriKind.Absolute, out var site))
        {
            foreach (var path in Paths.Take(MaxPages))
            {
                var url = new Uri(site, path).ToString();
                var page = await fetcher.FetchAsync(url, ct);
                checkedPages.Add(url);
                if (!page.Ok || string.IsNullOrEmpty(page.Content)) continue;
                // A redirect off the company's domain is not the company's page: ignore what it returned.
                if (ContactExtraction.HostOf(page.FinalUrl ?? url) is { } finalHost && finalHost != host && !finalHost.EndsWith("." + host, StringComparison.Ordinal)) continue;
                lists.Add(ContactExtraction.Extract(page.Content, page.FinalUrl ?? url, $"Published on {path}", host, job));
            }
        }

        var merged = ContactExtraction.Merge(lists);
        if (merged.Count == 0) note += " Nothing was published there: use a person you know, or the company's contact form.";
        return new FindContactsResult(merged, checkedPages, note);
    }
}
