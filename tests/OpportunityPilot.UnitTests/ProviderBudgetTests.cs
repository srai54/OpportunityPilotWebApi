using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Common;

namespace OpportunityPilot.UnitTests;

public sealed class ProviderBudgetTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Each_provider_stops_at_its_daily_cap_and_starts_again_the_next_day()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 16, 23, 0, 0, TimeSpan.Zero));
        var budget = new ProviderBudget(clock, Options.Create(new BudgetOptions { JsearchPerDay = 2, GroqPerDay = 1 }));
        Assert.True(budget.TryUse(ProviderBudget.Jsearch));
        Assert.True(budget.TryUse(ProviderBudget.Jsearch));
        Assert.False(budget.TryUse(ProviderBudget.Jsearch));
        Assert.Equal(2, budget.UsedToday(ProviderBudget.Jsearch));
        Assert.True(budget.TryUse(ProviderBudget.Groq));
        Assert.False(budget.TryUse(ProviderBudget.Groq));
        clock.Now = clock.Now.AddHours(2);
        Assert.True(budget.TryUse(ProviderBudget.Jsearch));
    }

    [Fact]
    public void A_zero_cap_switches_the_provider_off()
    {
        var budget = new ProviderBudget(TimeProvider.System, Options.Create(new BudgetOptions { ApifyPerDay = 0 }));
        Assert.False(budget.TryUse(ProviderBudget.Apify));
    }
}
