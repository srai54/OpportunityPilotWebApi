using OpportunityPilot.Application.Assistant;

namespace OpportunityPilot.UnitTests;

/// <summary>The voice "schedule" proposal: allowed shape, clamps, date window and matching the person's own deal or job.</summary>
public sealed class AssistantScheduleTests
{
    private static readonly DateTime LocalNow = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);

    private static AssistantActionDto? Action(string action, bool sales = false, DateTime? now = null) =>
        AssistantService.Parse($$"""{"reply":"Shall I schedule it?","action":{{action}}}""", sales, "Groq", now ?? LocalNow)?.Action;

    [Fact]
    public void A_complete_schedule_proposal_is_kept_with_its_fields()
    {
        var a = Action("""{"kind":"schedule","target":"Acme Labs","round":2,"when":"2026-10-16T15:00","mode":"phone","minutes":45,"label":"Schedule R2"}""");
        Assert.NotNull(a);
        Assert.Equal(("schedule", "Schedule R2", "Acme Labs", 2, "2026-10-16T15:00:00", "Phone", 45),
            (a.Kind, a.Label, a.Target, a.Round, a.When, a.Mode, a.Minutes));
        Assert.Null(a.TargetId);
    }

    [Fact]
    public void Optional_fields_may_be_missing_and_the_label_defaults()
    {
        var a = Action("""{"kind":"schedule","target":"Acme","when":"2026-10-16 15:00:00"}""", sales: true);
        Assert.Equal(("Schedule interview", (int?)null, (string?)null, (int?)null), (a?.Label, a?.Round, a?.Mode, a?.Minutes));
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(500, 240)]
    [InlineData(-30, 15)]
    [InlineData(60, 60)]
    public void Length_is_clamped_to_15_to_240_minutes(int said, int kept) =>
        Assert.Equal(kept, Action($$"""{"kind":"schedule","target":"Acme","when":"2026-10-16T15:00","minutes":{{said}}}""")?.Minutes);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 10)]
    [InlineData(3, 3)]
    public void Round_is_clamped_to_1_to_10(int said, int kept) =>
        Assert.Equal(kept, Action($$"""{"kind":"schedule","target":"Acme","when":"2026-10-16T15:00","round":{{said}}}""")?.Round);

    [Fact]
    public void An_unknown_mode_is_dropped_but_the_proposal_stays()
    {
        var a = Action("""{"kind":"schedule","target":"Acme","when":"2026-10-16T15:00","mode":"Carrier pigeon"}""");
        Assert.NotNull(a);
        Assert.Null(a.Mode);
    }

    [Theory]
    [InlineData("""{"kind":"schedule","when":"2026-10-16T15:00"}""")]                       // no target
    [InlineData("""{"kind":"schedule","target":"Acme"}""")]                                  // no time
    [InlineData("""{"kind":"schedule","target":"Acme","when":"tomorrow at 3"}""")]           // not ISO
    [InlineData("""{"kind":"schedule","target":"Acme","when":"2026-10-16T15:00+05:30"}""")]  // offset, not local
    [InlineData("""{"kind":"schedule","target":"Acme","when":"2026-10-14T15:00"}""")]        // in the past
    [InlineData("""{"kind":"schedule","target":"Acme","when":"2027-10-16T10:01"}""")]        // more than 365 days ahead
    [InlineData("""{"kind":"schedule","target":"Acme","when":2026}""")]                      // not a string
    public void A_proposal_without_a_target_or_a_valid_time_in_the_next_year_is_rejected(string action)
    {
        var r = AssistantService.Parse($$"""{"reply":"Which day?","action":{{action}}}""", false, "Groq", LocalNow);
        Assert.NotNull(r);
        Assert.Equal("Which day?", r.Reply);
        Assert.Null(r.Action);
    }

    [Fact]
    public void The_edges_of_the_window_are_allowed()
    {
        Assert.NotNull(Action("""{"kind":"schedule","target":"Acme","when":"2026-10-15T10:00"}"""));
        Assert.NotNull(Action("""{"kind":"schedule","target":"Acme","when":"2027-10-15T10:00"}"""));
    }

    [Fact]
    public void Without_the_local_time_a_schedule_cannot_be_checked_and_is_dropped() =>
        Assert.Null(AssistantService.Parse("""{"reply":"Ok?","action":{"kind":"schedule","target":"Acme","when":"2026-10-16T15:00"}}""", false)?.Action);

    // ---------- Matching the target ----------

    private static readonly ScheduleTarget AcmeSe = new(Guid.NewGuid(), "Software Engineer", "Acme Labs");
    private static readonly ScheduleTarget AcmeDs = new(Guid.NewGuid(), "Data Scientist", "Acme Labs");
    private static readonly ScheduleTarget Northwind = new(Guid.NewGuid(), "Java contractors Q4", "Northwind");

    [Fact]
    public void Matching_prefers_an_exact_name_and_ignores_case()
    {
        Assert.Equal([Northwind], AssistantService.MatchTargets("NORTHWIND", [AcmeSe, Northwind]));
        Assert.Equal([Northwind], AssistantService.MatchTargets("java contractors q4", [AcmeSe, Northwind]));
    }

    [Fact]
    public void Matching_falls_back_to_contains_and_to_a_name_said_inside_a_longer_phrase()
    {
        Assert.Equal([Northwind], AssistantService.MatchTargets("north", [AcmeSe, Northwind]));
        Assert.Equal([Northwind], AssistantService.MatchTargets("the Northwind Java role", [AcmeSe, Northwind]));
        Assert.Empty(AssistantService.MatchTargets("Globex", [AcmeSe, Northwind]));
        Assert.Empty(AssistantService.MatchTargets("  ", [AcmeSe, Northwind]));
    }

    [Fact]
    public void One_match_fills_the_target_id_and_type()
    {
        var reply = new AssistantReplyDto("Shall I?", new("schedule", "Schedule", null, null, null, Target: "Northwind"), "Groq", null);
        var sales = AssistantService.Resolve(reply, reply.Action!, [Northwind], sales: true);
        Assert.Equal((Northwind.Id, "deal"), (sales.Action?.TargetId, sales.Action?.TargetType));
        Assert.Equal("opportunity", AssistantService.Resolve(reply, reply.Action!, [AcmeSe], sales: false).Action?.TargetType);
    }

    [Fact]
    public void No_match_or_several_matches_ask_which_one_and_propose_nothing()
    {
        var reply = new AssistantReplyDto("Shall I?", new("schedule", "Schedule", null, null, null, Target: "Acme"), "Groq", null);
        var many = AssistantService.Resolve(reply, reply.Action!, AssistantService.MatchTargets("acme labs", [AcmeSe, AcmeDs, Northwind]), sales: false);
        Assert.Null(many.Action);
        Assert.Equal("I found 2 jobs for Acme: Acme Labs (Software Engineer), Acme Labs (Data Scientist). Which one?", many.Reply);
        var none = AssistantService.Resolve(reply, reply.Action!, [], sales: true);
        Assert.Null(none.Action);
        Assert.Equal("I couldn't find Acme in your deals. Which one do you mean?", none.Reply);
    }
}
