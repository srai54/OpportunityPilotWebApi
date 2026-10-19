using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace OpportunityPilot.Application.Common;

/// <summary>Daily caps for the paid or quota-limited providers, across all users. Render: Budgets__GroqPerDay and so on.</summary>
public sealed class BudgetOptions
{
    public const string Section = "Budgets";
    public int GroqPerDay { get; set; } = 1500;
    public int JsearchPerDay { get; set; } = 40;
    public int ApifyPerDay { get; set; } = 15;
}

/// <summary>
/// One counter per provider per UTC day, so a busy day (or a script) cannot use up a month's free quota. Kept in memory: a restart starts
/// the day's count again, which errs on the side of letting people work. Thread-safe; registered once.
/// </summary>
public sealed class ProviderBudget(TimeProvider clock, IOptions<BudgetOptions> options)
{
    public const string Groq = "groq", Jsearch = "jsearch", Apify = "apify";
    private readonly ConcurrentDictionary<string, (DateOnly Day, int Used)> counts = new(StringComparer.Ordinal);

    public int Limit(string provider) => provider switch
    {
        Groq => options.Value.GroqPerDay, Jsearch => options.Value.JsearchPerDay, Apify => options.Value.ApifyPerDay, _ => int.MaxValue,
    };

    /// <summary>Counts one call and returns true while today's cap is not reached; false (and nothing counted) once it is.</summary>
    public bool TryUse(string provider)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var limit = Limit(provider);
        var allowed = false;
        counts.AddOrUpdate(provider,
            _ => { allowed = limit > 0; return (today, allowed ? 1 : 0); },
            (_, current) =>
            {
                var used = current.Day == today ? current.Used : 0;
                allowed = used < limit;
                return (today, allowed ? used + 1 : used);
            });
        return allowed;
    }

    public int UsedToday(string provider) =>
        counts.TryGetValue(provider, out var c) && c.Day == DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ? c.Used : 0;

    public const string ExhaustedMessage = "Today's budget for this service is used up. It resets at midnight UTC; everything already found still works.";
}
