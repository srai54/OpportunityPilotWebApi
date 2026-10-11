namespace OpportunityPilot.Application.Trends;

/// <summary>One posting from the provider, reduced to the facts the trend report counts. No description is kept.</summary>
public sealed record TrendPosting(
    string Id, string Title, string Company, string? Site, string? City, bool IsRemote, string? EmploymentType,
    decimal? PayMin, decimal? PayMax, string? PayPeriod, DateTime? PostedAt, IReadOnlyList<string> Skills);

public sealed record TrendPageRequest(string Query, string? Location, string DatePosted, string? Country, string? Cursor);

public sealed record TrendPage(
    string Status, string? Message, IReadOnlyList<TrendPosting> Postings, string? NextCursor,
    int? QuotaRemaining, bool FromCache, DateTime? ObservedAt);

public interface ITrendSource
{
    bool Configured { get; }
    Task<TrendPage> PageAsync(TrendPageRequest request, CancellationToken ct);
}

public sealed record TrendCount(string Name, int Count, int SharePercent);

public sealed record TrendPay(string Period, int Postings, decimal MedianMin, decimal MedianMax);

public sealed record TrendFreshness(int Last24Hours, int Last3Days, int LastWeek, int Older, int Unknown);

public sealed record TrendReport(
    string Status, string? Message, int Sampled, int Pages,
    IReadOnlyList<TrendCount> Sites, IReadOnlyList<TrendCount> TopSkills, IReadOnlyList<TrendCount> TopCompanies,
    IReadOnlyList<TrendCount> Locations, IReadOnlyList<TrendCount> EmploymentTypes,
    int RemotePercent, int PayListedPercent, IReadOnlyList<TrendPay> Pay, TrendFreshness Freshness,
    DateTime? ObservedAt, int? QuotaRemaining, bool FromCache, string Source);
