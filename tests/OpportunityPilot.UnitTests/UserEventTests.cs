using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.UnitTests;

public class UserEventTests
{
    [Fact]
    public void An_event_keeps_a_short_safe_summary_and_may_point_at_an_opportunity()
    {
        var opp = Guid.NewGuid();
        var e = new UserEvent(Guid.NewGuid(), UserEventKinds.DraftApproved, DateTime.UtcNow, new string('x', 900), opp, "  Backend Engineer  ");
        Assert.Equal(UserEvent.MaxDetailLength, e.Detail.Length);
        Assert.EndsWith("…", e.Detail);
        Assert.Equal("Backend Engineer", e.Title);
        Assert.Equal(opp, e.OpportunityId);
        Assert.Null(new UserEvent(Guid.NewGuid(), UserEventKinds.Searched, DateTime.UtcNow, "Searched all free sites").OpportunityId);
    }

    [Fact]
    public void An_unknown_kind_or_a_missing_owner_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UserEvent(Guid.NewGuid(), "Invented", DateTime.UtcNow, "x"));
        Assert.Throws<ArgumentException>(() => new UserEvent(Guid.Empty, UserEventKinds.Searched, DateTime.UtcNow, "x"));
    }

    [Fact]
    public void Every_kind_the_feed_can_filter_on_is_known()
    {
        Assert.Equal(UserEventKinds.All.Count, UserEventKinds.All.Distinct().Count());
        Assert.All(UserEventKinds.All, k => Assert.False(string.IsNullOrWhiteSpace(k)));
    }
}
