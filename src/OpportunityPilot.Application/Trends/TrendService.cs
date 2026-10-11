namespace OpportunityPilot.Application.Trends;

/// <summary>Samples up to three pages (one provider search each) and counts what the postings say.</summary>
public sealed class TrendService(ITrendSource source, TimeProvider clock)
{
    public const int MaxPages = 3;
    public bool Configured => source.Configured;

    public async Task<TrendReport> ReportAsync(string query, string? location, string datePosted, string? country, int pages, CancellationToken ct)
    {
        pages = Math.Clamp(pages, 1, MaxPages);
        var all = new List<TrendPosting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        TrendPage? last = null;
        var used = 0;
        var fromCache = true;
        for (var i = 0; i < pages; i++)
        {
            var page = await source.PageAsync(new(query, location, datePosted, country, cursor), ct);
            if (page.Status != "Ready")
                return i == 0 ? Empty(page) : Finish(all, used, last!, fromCache);
            last = page;
            used++;
            fromCache &= page.FromCache;
            foreach (var p in page.Postings) if (seen.Add(p.Id)) all.Add(p);
            cursor = page.NextCursor;
            if (cursor is null) break;
        }
        return Finish(all, used, last!, fromCache);
    }

    private TrendReport Finish(List<TrendPosting> all, int pages, TrendPage last, bool fromCache) =>
        TrendAnalyzer.Analyze(all, pages, clock.GetUtcNow().UtcDateTime, last.ObservedAt, last.QuotaRemaining, fromCache,
            "JSearch (Google for Jobs): the newest postings it returns for this search, from every site it covers");

    private static TrendReport Empty(TrendPage page) =>
        new(page.Status, page.Message, 0, 0, [], [], [], [], [], 0, 0, [], new(0, 0, 0, 0, 0), page.ObservedAt, page.QuotaRemaining, false, "JSearch");
}
