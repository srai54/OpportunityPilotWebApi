using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Research.Boards;

/// <summary>One job from a Greenhouse board listing (no description: the list endpoint is read without content).</summary>
public sealed record GreenhouseJob(string Id, string Title, string? Url, string? Location, string? Company, DateTime? UpdatedAt);

/// <summary>
/// Pure JSON → <see cref="Candidate"/> mapping for the public job-board APIs. Every reader is tolerant: missing or
/// null fields are skipped, a posting without an id or title is dropped, and a body that is not the documented shape
/// returns null (the caller records a safe "could not be read" reason). Descriptions that arrive as HTML are reduced
/// to text with <see cref="IContentParser"/> (scripting off, no external resources).
/// </summary>
public static class BoardMapping
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "for", "with", "of", "in", "at", "to", "or", "on", "by"
    };

    // ---------- Greenhouse ----------

    /// <summary><c>{ jobs: [{ id, title, absolute_url, location: { name }, updated_at, company_name }] }</c>.</summary>
    public static IReadOnlyList<GreenhouseJob>? GreenhouseJobs(string? json)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<GreenhouseJob>();
        foreach (var job in jobs.EnumerateArray())
        {
            var id = Text(job, "id");
            var title = Text(job, "title");
            if (id is null || title is null) continue;
            result.Add(new GreenhouseJob(id, title, HttpUrl(Text(job, "absolute_url")), Text(Child(job, "location"), "name"),
                Text(job, "company_name"), Date(Text(job, "updated_at"))));
        }
        return result;
    }

    /// <summary>The job detail's <c>content</c>: HTML that Greenhouse sends entity-escaped, so it is unescaped first, then reduced to text.</summary>
    public static string? GreenhouseDescription(string? json, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root) return null;
        var content = Text(root, "content");
        return content is null ? null : HtmlToText(WebUtility.HtmlDecode(content), parser);
    }

    public static Candidate GreenhouseCandidate(Source source, string token, GreenhouseJob job, string? description)
    {
        var organization = job.Company ?? token;
        return Build(source, JobPlatform.Greenhouse, job.Id, job.Title, organization, job.Location, null, job.Url, job.Url, null, description,
            job.UpdatedAt);
    }

    // ---------- Lever ----------

    /// <summary>
    /// <c>[{ id, text, hostedUrl, applyUrl, categories: { location }, workplaceType, descriptionPlain, lists: [{ text, content }], country }]</c>.
    /// </summary>
    public static IReadOnlyList<Candidate>? LeverCandidates(string? json, Source source, string slug, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Array } postings) return null;

        var organization = TitleCase(slug);
        var result = new List<Candidate>();
        foreach (var p in postings.EnumerateArray())
        {
            var id = Text(p, "id");
            var title = Text(p, "text");
            if (id is null || title is null) continue;

            var description = new StringBuilder(Text(p, "descriptionPlain") ?? string.Empty);
            if (Child(p, "lists") is { ValueKind: JsonValueKind.Array } lists)
            {
                foreach (var list in lists.EnumerateArray())
                {
                    var heading = Text(list, "text");
                    var items = Text(list, "content") is { } html ? HtmlToText(html, parser) : null;
                    if (heading is null && items is null) continue;
                    description.Append("\n\n").Append(heading).Append(heading is not null && items is not null ? "\n" : "").Append(items);
                }
            }
            var hostedUrl = HttpUrl(Text(p, "hostedUrl"));
            var workplaceType = Text(p, "workplaceType");
            result.Add(Build(source, JobPlatform.Lever, id, title, organization, Text(Child(p, "categories"), "location"),
                workplaceType, hostedUrl, HttpUrl(Text(p, "applyUrl")) ?? hostedUrl, Text(p, "country"),
                description.ToString().Trim(), EpochMilliseconds(p, "createdAt")));
        }
        return result;
    }

    // ---------- Adzuna ----------

    /// <summary>
    /// <c>{ results: [{ id, title, company: { display_name }, location: { display_name }, redirect_url, description, created, salary_min, salary_max, salary_is_predicted }] }</c>.
    /// Adzuna gives no currency (it is the country's own), so pay is shown as plain numbers, marked when Adzuna estimated it.
    /// </summary>
    public static IReadOnlyList<Candidate>? AdzunaCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<Candidate>();
        foreach (var r in results.EnumerateArray())
        {
            var id = Text(r, "id");
            // Adzuna marks the search words with <strong> in titles and snippets.
            var title = Text(r, "title") is { } t ? HtmlToText(t, parser) : null;
            if (id is null || string.IsNullOrEmpty(title)) continue;
            var url = HttpUrl(Text(r, "redirect_url"));
            var description = Text(r, "description") is { } d ? HtmlToText(d, parser) : null;
            var pay = Child(r, "salary_min") is { ValueKind: JsonValueKind.Number } min && Child(r, "salary_max") is { ValueKind: JsonValueKind.Number } max
                ? string.Create(CultureInfo.InvariantCulture, $"Salary: {min.GetDecimal():0} - {max.GetDecimal():0}") +
                  (Text(r, "salary_is_predicted") == "1" ? " (Adzuna estimate)" : "")
                : null;
            result.Add(Build(source, JobPlatform.Adzuna, id, title, Text(Child(r, "company"), "display_name") ?? string.Empty,
                Text(Child(r, "location"), "display_name"), null, url, url, null, Lines(pay, description), Date(Text(r, "created"))));
        }
        return result;
    }

    // ---------- The Muse ----------

    /// <summary>
    /// The Muse's public jobs API (one page): <c>{ page, page_count, results: [{ id, name, contents, publication_date, locations: [{ name }],
    /// levels: [{ name }], company: { name }, refs: { landing_page } }] }</c>. A posting without a title or an http(s) link is dropped.
    /// </summary>
    public static IReadOnlyList<Candidate>? TheMuseCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "results") is not { ValueKind: JsonValueKind.Array } rows) return null;
        var result = new List<Candidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var title = Text(row, "name");
            var url = HttpUrl(Text(Child(row, "refs"), "landing_page"));
            if (title is null || url is null) continue;
            var places = Names(Child(row, "locations"));
            var levels = Names(Child(row, "levels"));
            var remote = places.Any(p => p.Contains("remote", StringComparison.OrdinalIgnoreCase));
            var description = Text(row, "contents") is { } html ? HtmlToText(html, parser) : null;
            var level = levels.Count > 0 ? "Level: " + string.Join(", ", levels) : null;
            result.Add(Build(source, JobPlatform.TheMuse, Text(row, "id") ?? url, title, Text(Child(row, "company"), "name") ?? string.Empty,
                places.Count == 0 ? null : string.Join("; ", places), remote ? "remote" : null, url, url, null,
                Lines(level, description), Date(Text(row, "publication_date"))));
        }
        return result;
    }

    /// <summary>The non-blank parts, one per line; null when there are none.</summary>
    private static string? Lines(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    /// <summary>The <c>name</c> of every object in a JSON array (The Muse's locations, levels); empty when it is not an array.</summary>
    private static List<string> Names(JsonElement? array) => array is { ValueKind: JsonValueKind.Array } a
        ? a.EnumerateArray().Select(e => Text(e, "name")).Where(n => n is not null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    // ---------- Additional public boards ----------

    /// <summary>Ashby's public posting feed: <c>{ jobs: [{ id, title, location, workplaceType, jobUrl, applyUrl, descriptionPlain }] }</c>.</summary>
    public static IReadOnlyList<Candidate>? AshbyCandidates(string? json, Source source, string slug, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "jobs") is not { ValueKind: JsonValueKind.Array } jobs) return null;
        var result = new List<Candidate>();
        foreach (var job in jobs.EnumerateArray())
        {
            var id = Text(job, "id");
            var title = Text(job, "title");
            if (id is null || title is null) continue;
            var description = Text(job, "descriptionPlain") ??
                              (Text(job, "descriptionHtml") is { } html ? HtmlToText(html, parser) : null);
            var url = HttpUrl(Text(job, "jobUrl"));
            result.Add(Build(source, JobPlatform.Ashby, id, title, TitleCase(slug), Text(job, "location"),
                Text(job, "workplaceType"), url, HttpUrl(Text(job, "applyUrl")) ?? url, null, description,
                Date(Text(job, "publishedAt"))));
        }
        return result;
    }

    /// <summary>SmartRecruiters public posting list. List rows are honest title/location candidates; detail is optional.</summary>
    public static IReadOnlyList<Candidate>? SmartRecruitersCandidates(string? json, Source source, string slug, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "content") is not { ValueKind: JsonValueKind.Array } rows) return null;
        var result = new List<Candidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, "id") ?? Text(row, "uuid");
            var title = Text(row, "name");
            if (id is null || title is null) continue;
            var company = Text(Child(row, "company"), "name") ?? TitleCase(slug);
            var location = JoinLocation(Child(row, "location"));
            var remote = Child(Child(row, "location"), "remote") is { ValueKind: JsonValueKind.True } ? "remote" : null;
            var url = $"https://jobs.smartrecruiters.com/{slug}/{id}";
            var description = Text(row, "jobAd") is { } ad ? HtmlToText(ad, parser) : null;
            result.Add(Build(source, JobPlatform.SmartRecruiters, id, title, company, location, remote, url, url,
                Text(Child(row, "location"), "country"), description, Date(Text(row, "releasedDate"))));
        }
        return result;
    }

    /// <summary>Recruitee's careers feed: <c>{ offers: [...] }</c>.</summary>
    public static IReadOnlyList<Candidate>? RecruiteeCandidates(string? json, Source source, string slug, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "offers") is not { ValueKind: JsonValueKind.Array } offers) return null;
        var result = new List<Candidate>();
        foreach (var offer in offers.EnumerateArray())
        {
            var id = Text(offer, "id") ?? Text(offer, "slug");
            var title = Text(offer, "title");
            if (id is null || title is null) continue;
            var url = HttpUrl(Text(offer, "careers_url"));
            var description = Text(offer, "description") is { } html ? HtmlToText(html, parser) : null;
            result.Add(Build(source, JobPlatform.Recruitee, id, title, Text(offer, "company_name") ?? TitleCase(slug),
                Text(offer, "location"), Text(offer, "workplace_type"), url,
                HttpUrl(Text(offer, "careers_apply_url")) ?? url, Text(offer, "country"), description,
                Date(Text(offer, "published_at"))));
        }
        return result;
    }

    /// <summary>Workable's no-auth widget response (either an array or an object containing <c>jobs</c>/<c>results</c>).</summary>
    public static IReadOnlyList<Candidate>? WorkableCandidates(string? json, Source source, string slug, IContentParser parser)
    {
        using var document = Parse(json);
        if (document is null) return null;
        var root = document.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root : Child(root, "jobs") ?? Child(root, "results");
        if (rows is not { ValueKind: JsonValueKind.Array }) return null;
        var result = new List<Candidate>();
        foreach (var row in rows.Value.EnumerateArray())
        {
            var id = Text(row, "shortcode") ?? Text(row, "id");
            var title = Text(row, "title");
            if (id is null || title is null) continue;
            var location = Text(Child(row, "location"), "location_str") ?? Text(row, "location") ??
                JoinParts(Text(row, "city"), Text(row, "state"), Text(row, "country"));
            var url = HttpUrl(Text(row, "url")) ?? $"https://apply.workable.com/{slug}/j/{id}/";
            var description = Text(row, "description") is { } html ? HtmlToText(html, parser) : null;
            result.Add(Build(source, JobPlatform.Workable, id, title, TitleCase(slug), location,
                Child(row, "telecommuting") is { ValueKind: JsonValueKind.True } ? "remote" : Text(row, "workplace_type"),
                url, HttpUrl(Text(row, "application_url")) ?? url, Text(row, "country"), description,
                Date(Text(row, "published_on"))));
        }
        return result;
    }

    public static IReadOnlyList<Candidate>? RemotiveCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "jobs") is not { ValueKind: JsonValueKind.Array } jobs) return null;
        return Aggregate(jobs, source, JobPlatform.Remotive, parser, "id", "title", "company_name",
            "candidate_required_location", "url", "description", "publication_date");
    }

    public static IReadOnlyList<Candidate>? RemoteOkCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Array } rows) return null;
        return Aggregate(rows, source, JobPlatform.RemoteOk, parser, "id", "position", "company", "location", "url", "description", "date");
    }

    /// <summary>Arbeitnow's free job-board API: <c>{ data: [ { slug, company_name, title, description, remote, url, tags, job_types, location, created_at } ] }</c>.</summary>
    public static IReadOnlyList<Candidate>? ArbeitnowCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "data") is not { ValueKind: JsonValueKind.Array } rows) return null;
        var result = new List<Candidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var slug = Text(row, "slug");
            var title = Text(row, "title");
            if (slug is null || title is null) continue;
            var url = HttpUrl(Text(row, "url"));
            var description = Text(row, "description") is { } html ? HtmlToText(html, parser) : null;
            var remote = Child(row, "remote") is { ValueKind: JsonValueKind.True };
            result.Add(Build(source, JobPlatform.Arbeitnow, slug, title, Text(row, "company_name") ?? string.Empty, Text(row, "location"),
                remote ? "remote" : null, url, url, null, description, UnixDate(row, "created_at")));
        }
        return result;
    }

    /// <summary>Jobicy's free remote-jobs API: <c>{ jobs: [ { id, url, jobTitle, companyName, jobGeo, jobDescription, pubDate, salary… } ] }</c>.</summary>
    public static IReadOnlyList<Candidate>? JobicyCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "jobs") is not { ValueKind: JsonValueKind.Array } rows) return null;
        var result = new List<Candidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, "id");
            var title = Text(row, "jobTitle");
            if (id is null || title is null) continue;
            var url = HttpUrl(Text(row, "url"));
            var description = Text(row, "jobDescription") is { } html ? HtmlToText(html, parser) : Text(row, "jobExcerpt");
            var pay = Child(row, "salaryMin") is { ValueKind: JsonValueKind.Number } min && Child(row, "salaryMax") is { ValueKind: JsonValueKind.Number } max
                ? $"Salary: {Text(row, "salaryCurrency")} {min.GetDecimal():0} - {max.GetDecimal():0} {Text(row, "salaryPeriod")}".Trim() : null;
            result.Add(Build(source, JobPlatform.Jobicy, id, title, Text(row, "companyName") ?? string.Empty, Text(row, "jobGeo"),
                "remote", url, url, null, pay is null ? description : pay + "\n" + description, Date(Text(row, "pubDate"))));
        }
        return result;
    }

    /// <summary>Himalayas' free remote-jobs search: <c>{ jobs: [ { title, companyName, excerpt, description, employmentType, minSalary, maxSalary, currency, locationRestrictions[], pubDate, applicationLink } ] }</c>.</summary>
    public static IReadOnlyList<Candidate>? HimalayasCandidates(string? json, Source source, IContentParser parser)
    {
        using var document = Parse(json);
        if (document?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Child(root, "jobs") is not { ValueKind: JsonValueKind.Array } rows) return null;
        var result = new List<Candidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var title = Text(row, "title");
            var url = HttpUrl(Text(row, "applicationLink") ?? Text(row, "guid"));
            if (title is null || url is null) continue;
            var places = Child(row, "locationRestrictions") is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Select(e => e.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList() : [];
            var where = places.Count == 0 ? "Remote (worldwide)" : places.Count > 6 ? $"Remote ({places.Count} countries)" : "Remote — " + string.Join(", ", places);
            var description = Text(row, "description") is { } html ? HtmlToText(html, parser) : Text(row, "excerpt") ?? string.Empty;
            var pay = Child(row, "minSalary") is { ValueKind: JsonValueKind.Number } min && Child(row, "maxSalary") is { ValueKind: JsonValueKind.Number } max
                ? $"Salary: {Text(row, "currency")} {min.GetDecimal():0} - {max.GetDecimal():0} {Text(row, "salaryPeriod")}".Trim() : null;
            var open = places.Count > 0 ? "\nOpen to: " + string.Join(", ", places) : string.Empty;
            result.Add(Build(source, JobPlatform.Other, url, title, Text(row, "companyName") ?? string.Empty, where, "remote", url, url, null,
                (pay is null ? description : pay + "\n" + description) + open, UnixDate(row, "pubDate")));
        }
        return result;
    }

    /// <summary>
    /// We Work Remotely's public RSS feed. An item's title is "Company: Job title"; its link is the posting. DTDs are refused
    /// so a hostile feed cannot expand entities.
    /// </summary>
    public static IReadOnlyList<Candidate>? WeWorkRemotelyCandidates(string? xml, Source source, IContentParser parser)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        System.Xml.Linq.XDocument doc;
        try
        {
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0
            });
            doc = System.Xml.Linq.XDocument.Load(reader);
        }
        catch (System.Xml.XmlException) { return null; }
        var items = doc.Descendants("item").ToList();
        if (doc.Root is null) return null;
        var result = new List<Candidate>();
        foreach (var item in items)
        {
            string? Get(string name) => item.Element(name)?.Value;
            var raw = Get("title")?.Trim();
            var url = HttpUrl(Get("link")?.Trim());
            var guid = Get("guid")?.Trim() ?? url;
            if (string.IsNullOrEmpty(raw) || guid is null) continue;
            var colon = raw.IndexOf(':');
            var company = colon > 0 ? raw[..colon].Trim() : string.Empty;
            var title = colon > 0 ? raw[(colon + 1)..].Trim() : raw;
            var description = Get("description") is { } html ? HtmlToText(html, parser) : null;
            DateTime? posted = DateTime.TryParse(Get("pubDate"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
            result.Add(Build(source, JobPlatform.WeWorkRemotely, guid, title, company, Get("region"), "remote", url, url, null, description, posted));
        }
        return result;
    }

    /// <summary>A JSON number of Unix seconds as UTC; null when absent or not a number.</summary>
    private static DateTime? UnixDate(JsonElement row, string name) =>
        Child(row, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;

    private static IReadOnlyList<Candidate> Aggregate(JsonElement rows, Source source, JobPlatform platform, IContentParser parser,
        string idName, string titleName, string companyName, string locationName, string urlName, string descriptionName, string dateName)
    {
        var result = new List<Candidate>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, idName);
            var title = Text(row, titleName);
            if (id is null || title is null) continue; // Remote OK's first element is legal metadata, not a job.
            var url = HttpUrl(Text(row, urlName));
            var description = Text(row, descriptionName) is { } html ? HtmlToText(html, parser) : null;
            result.Add(Build(source, platform, id, title, Text(row, companyName) ?? string.Empty, Text(row, locationName),
                null, url, url, null, description, Date(Text(row, dateName))));
        }
        return result;
    }

    // ---------- Title pre-filter ----------

    /// <summary>
    /// The terms a job title is matched against before descriptions are fetched: each campaign keyword phrase, each
    /// word of those phrases (common short words dropped) and each required skill. Empty when none are configured.
    /// </summary>
    public static IReadOnlyList<string> TitleTerms(CampaignCriteria criteria)
    {
        var terms = new List<string>();
        void Add(string term)
        {
            term = term.Trim();
            if (term.Length > 0 && !terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        }
        foreach (var keyword in criteria.Keywords)
        {
            Add(keyword);
            foreach (var word in keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (word.Length > 1 && !StopWords.Contains(word)) Add(word);
        }
        foreach (var skill in criteria.RequiredSkills) Add(skill);
        return terms;
    }

    /// <summary>
    /// Keeps jobs whose title contains any term (word-boundary match with the skill aliases of the rules); keeps all
    /// when there are no terms. The best matches (most terms) come first, then the most recently updated, so a small
    /// fetch budget is spent on the likeliest jobs.
    /// </summary>
    public static List<GreenhouseJob> Prefilter(IReadOnlyList<GreenhouseJob> jobs, IReadOnlyList<string> terms) =>
        jobs.Select((job, index) => (job, index, hits: terms.Count == 0 ? 0 : terms.Count(t => TextMatch.Contains(job.Title, t))))
            .Where(x => terms.Count == 0 || x.hits > 0)
            .OrderByDescending(x => x.hits)
            .ThenByDescending(x => x.job.UpdatedAt ?? DateTime.MinValue)
            .ThenBy(x => x.index)
            .Select(x => x.job)
            .ToList();

    // ---------- helpers ----------

    /// <summary>
    /// A live board posting (JSearch) as a job candidate. The board link is the evidence and apply URL. A LinkedIn posting
    /// keeps LinkedIn's own job id (from <c>/jobs/view/…{id}</c>) so it de-duplicates with the agent's finds and, once
    /// shortlisted, the local agent can apply to it.
    /// </summary>
    public static Candidate JobSearchCandidate(Source source, JobBoard board, JobBoardJobDto job)
    {
        var (platform, externalId) = board switch
        {
            JobBoard.LinkedIn => (JobPlatform.LinkedIn, LinkedInJobId(job.BoardUrl) ?? job.ProviderJobId),
            JobBoard.Seek => (JobPlatform.Seek, job.ProviderJobId),
            _ => (JobPlatform.Indeed, job.ProviderJobId),
        };
        var url = platform == JobPlatform.LinkedIn && LinkedInJobId(job.BoardUrl) is { } id ? $"https://www.linkedin.com/jobs/view/{id}/" : job.BoardUrl;
        return Build(source, platform, externalId, job.Title, job.CompanyName, job.Location, job.IsRemote ? "remote" : null,
            url, url, null, job.Description, job.PostedAt);
    }

    /// <summary>
    /// For Sales campaigns: a company that is hiring is a lead. One candidate per company; each of its postings is a line of
    /// evidence (role, place, date, board link) followed by the posting text, so buying signals are matched against it.
    /// </summary>
    public static Candidate HiringCompanyCandidate(Source source, JobBoard board, string company, IReadOnlyList<JobBoardJobDto> postings)
    {
        var name = JsearchBoardParser.Name(board);
        var lines = postings.Select(p => $"Hiring on {name}: {p.Title}" +
            (p.Location is null ? "" : $" — {p.Location}") + (p.IsRemote ? " (remote)" : "") +
            (p.PostedAt is { } d ? $" — posted {d:yyyy-MM-dd}" : "") + $" — {p.BoardUrl}").ToList();
        var details = string.Join("\n\n", postings.Select(p => p.Description).Where(t => !string.IsNullOrWhiteSpace(t)));
        var text = Candidates.Bound(string.Join("\n", lines) + (details.Length > 0 ? "\n\n" + details : ""));
        var first = postings[0];
        return new Candidate(source.Id, source.Label, company, company, first.Location, first.BoardUrl, null, null, text,
            null, null, null, first.BoardUrl, $"{company}\n{text}", postings.Select(p => p.BoardUrl).Distinct().ToList());
    }

    private static string? LinkedInJobId(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith("linkedin.com", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(u.AbsolutePath, @"/jobs/view/(?:[^/]*?-)?(\d{6,})/?$");
        if (m.Success) return m.Groups[1].Value;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query)["currentJobId"];
        return q is { Length: >= 6 } && q.All(char.IsAsciiDigit) ? q : null;
    }

    /// <summary>The shared candidate builder, for boards mapped outside this class (Workday).</summary>
    public static Candidate BuildCandidate(Source source, JobPlatform platform, string externalId, string title, string organization,
        string? location, string? workplaceType, string? url, string? applyUrl, string? country, string? description,
        DateTime? postedAt = null) =>
        Build(source, platform, externalId, title, organization, location, workplaceType, url, applyUrl, country, description, postedAt);

    private static Candidate Build(Source source, JobPlatform platform, string externalId, string title, string organization,
        string? location, string? workplaceType, string? url, string? applyUrl, string? country, string? description,
        DateTime? postedAt = null)
    {
        var workplace = WorkplaceHint(workplaceType);
        var text = Candidates.Bound(string.Join("\n", new[] { workplace is null ? null : $"Workplace: {workplace}", description }
            .Where(x => !string.IsNullOrWhiteSpace(x))));
        var lines = new List<string> { title };
        if (organization.Length > 0) lines.Add($"Company: {organization}");
        if (location is not null) lines.Add($"Location: {location}");
        if (workplace is not null) lines.Add($"Workplace: {workplace}");
        if (url is not null) lines.Add($"URL: {url}");
        if (!string.IsNullOrWhiteSpace(description)) lines.Add("\n" + description);
        return new Candidate(source.Id, source.Label, title, organization, location, url, platform, externalId, text, null, country, null,
            url, string.Join('\n', lines), [], applyUrl, workplaceType, postedAt);
    }

    /// <summary>Lever's <c>workplaceType</c> in words the work-mode rule recognises; unknown values give no hint.</summary>
    private static string? WorkplaceHint(string? workplaceType) => workplaceType?.Trim().ToLowerInvariant() switch
    {
        "remote" => "Remote",
        "hybrid" => "Hybrid",
        "on-site" or "onsite" => "On-site",
        _ => null
    };

    private static string HtmlToText(string html, IContentParser parser) =>
        html.Contains('<') || html.Contains('&') ? parser.ParseHtml(html, null).Text : html.Trim();

    /// <summary>"acme-corp" → "Acme Corp".</summary>
    public static string TitleCase(string slug) =>
        string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Child(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var child) ? child : null;

    /// <summary>A string, or a number's literal text (ids are numbers on Greenhouse, strings on Adzuna); blank → null.</summary>
    private static string? Text(JsonElement? element, string name) => Child(element, name) switch
    {
        { ValueKind: JsonValueKind.String } s when !string.IsNullOrWhiteSpace(s.GetString()) => s.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null
    };

    private static string? HttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? url : null;

    private static DateTime? Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : null;

    private static DateTime? EpochMilliseconds(JsonElement element, string name) => Child(element, name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt64(out var value) =>
            DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime,
        _ => null
    };

    private static string? JoinLocation(JsonElement? location)
    {
        if (location is not { ValueKind: JsonValueKind.Object } value) return null;
        var parts = new[] { Text(value, "city"), Text(value, "region"), Text(value, "country") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? JoinParts(params string?[] values)
    {
        var parts = values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
