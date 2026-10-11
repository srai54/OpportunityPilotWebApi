namespace OpportunityPilot.Application.Trends;

/// <summary>Counts what a sample of postings says. A sample is the newest postings the provider returned, not the whole market.</summary>
public static class TrendAnalyzer
{
    public static TrendReport Analyze(IReadOnlyList<TrendPosting> postings, int pages, DateTime now, DateTime? observedAt, int? quota, bool fromCache, string source)
    {
        var n = postings.Count;
        return new("Ready", n == 0 ? "No postings matched. Try a broader role or a bigger place." : null, n, pages,
            Top(postings.Select(p => p.Site), n, 8), Top(postings.SelectMany(p => p.Skills), n, 15),
            Top(postings.Select(p => p.Company), n, 10), Top(postings.Select(p => p.City), n, 8),
            Top(postings.Select(p => Label(p.EmploymentType)), n, 5),
            Percent(postings.Count(p => p.IsRemote), n), Percent(postings.Count(p => p.PayMin is not null || p.PayMax is not null), n),
            Pay(postings), Freshness(postings, now), observedAt, quota, fromCache, source);
    }

    public static string? Label(string? type) => string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToUpperInvariant() switch
    {
        "FULLTIME" or "FULL-TIME" => "Full time",
        "PARTTIME" or "PART-TIME" => "Part time",
        "CONTRACTOR" or "CONTRACT" => "Contract",
        "INTERN" or "INTERNSHIP" => "Internship",
        _ => type.Trim(),
    };

    private static int Percent(int part, int whole) => whole == 0 ? 0 : (int)Math.Round(100.0 * part / whole);

    private static IReadOnlyList<TrendCount> Top(IEnumerable<string?> values, int total, int take) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(x => x.Count).ThenBy(x => x.Key).Take(take)
            .Select(x => new TrendCount(x.Key, x.Count, Percent(x.Count, total))).ToList();

    private static IReadOnlyList<TrendPay> Pay(IReadOnlyList<TrendPosting> postings) =>
        postings.Where(p => (p.PayMin ?? p.PayMax) is not null && !string.IsNullOrWhiteSpace(p.PayPeriod))
            .GroupBy(p => p.PayPeriod!.ToUpperInvariant())
            .Select(g => new TrendPay(Period(g.Key), g.Count(),
                Median(g.Select(p => p.PayMin ?? p.PayMax!.Value)), Median(g.Select(p => p.PayMax ?? p.PayMin!.Value))))
            .OrderByDescending(p => p.Postings).ToList();

    private static string Period(string p) => p switch
    {
        "HOUR" => "per hour", "DAY" => "per day", "WEEK" => "per week", "MONTH" => "per month", "YEAR" => "per year", _ => p.ToLowerInvariant(),
    };

    private static decimal Median(IEnumerable<decimal> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        return s.Length == 0 ? 0 : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    private static TrendFreshness Freshness(IReadOnlyList<TrendPosting> postings, DateTime now)
    {
        int d1 = 0, d3 = 0, d7 = 0, older = 0, unknown = 0;
        foreach (var p in postings)
        {
            if (p.PostedAt is null) { unknown++; continue; }
            var age = now - p.PostedAt.Value;
            if (age <= TimeSpan.FromHours(36)) d1++;
            else if (age <= TimeSpan.FromDays(3.5)) d3++;
            else if (age <= TimeSpan.FromDays(7.5)) d7++;
            else older++;
        }
        return new(d1, d3, d7, older, unknown);
    }
}
