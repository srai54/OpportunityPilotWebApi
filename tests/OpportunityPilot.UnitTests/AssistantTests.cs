using OpportunityPilot.Application.Assistant;

namespace OpportunityPilot.UnitTests;

public sealed class AssistantTests
{
    private static readonly Dictionary<string, int> Facts = new()
    {
        ["jobsFoundThisWeek"] = 12, ["goodMatches"] = 4, ["waitingForApproval"] = 3, ["approvedNotYetActedOn"] = 1,
        ["messagesToCheck"] = 2, ["applied"] = 5, ["replies"] = 1, ["agentNeedsYou"] = 0,
        ["messagesSentToday"] = 3, ["emailsSentToday"] = 2, ["messagesSentThisWeek"] = 11,
    };

    [Fact]
    public void Without_ai_a_status_question_gets_the_summary_from_real_numbers()
    {
        var r = AssistantService.Rules("what is my status", Facts, sales: false, "NoKey");
        Assert.Equal("This week 12 jobs found, 4 good matches, 3 waiting for your approval, 2 messages to check, 5 applied and 1 reply, 3 sent today.", r.Reply);
        Assert.Null(r.Action);
        Assert.Equal("Rules", r.Source);
    }

    [Fact]
    public void Without_ai_a_search_request_proposes_launching_it()
    {
        var r = AssistantService.Rules("find java developer jobs in Pune", Facts, sales: false, "NoKey");
        Assert.Equal("launch", r.Action?.Kind);
        Assert.Equal("find java developer jobs in Pune", r.Action?.Query);
    }

    [Fact]
    public void Interviews_open_the_sales_tracker_for_sales()
    {
        Assert.Equal("/staffing/interviews", AssistantService.Rules("show interviews", Facts, sales: true, null).Action?.Page);
        Assert.Equal("/interviews", AssistantService.Rules("show interviews", Facts, sales: false, null).Action?.Page);
    }

    [Fact]
    public void Model_answer_is_kept_only_in_the_allowed_shape()
    {
        var ok = AssistantService.Parse("""{"reply":"Starting it.","action":{"kind":"launch","query":"java developer","location":"Pune","label":"Search Java jobs"}}""", false, "Groq");
        Assert.Equal("launch", ok?.Action?.Kind);
        Assert.Equal("Pune", ok?.Action?.Location);
        Assert.Equal("Groq", ok?.Source);
        var unknownPage = AssistantService.Parse("""{"reply":"Here.","action":{"kind":"open","page":"admin"}}""", false);
        Assert.NotNull(unknownPage);
        Assert.Null(unknownPage!.Action);
        Assert.Null(AssistantService.Parse("""{"action":null}""", false));
        Assert.Null(AssistantService.Parse("not json", false));
    }

    [Fact]
    public void Without_ai_how_many_sent_today_is_answered_from_todays_counts()
    {
        var r = AssistantService.Rules("how many mails sent today", Facts, sales: false, "NoKey");
        Assert.Equal("You sent 3 messages today (2 by email) and 11 this week.", r.Reply);
        Assert.Null(r.Action);
    }

    [Fact]
    public void Without_ai_a_launch_keeps_the_posting_age_and_follow_ups_map_to_the_right_flow()
    {
        Assert.Equal(3, AssistantService.Rules("launch campaign of software engineer jobs in past 3 days in ggn", Facts, false, null).Action?.Days);
        Assert.Equal("outreach", AssistantService.Rules("send the outreach", Facts, sales: true, null).Action?.Kind);
        Assert.Equal("apply", AssistantService.Rules("apply to them", Facts, sales: false, null).Action?.Kind);
        Assert.Equal("review", AssistantService.Rules("collect everything, I will review", Facts, false, null).Action?.Kind);
        Assert.Equal("/outreach", AssistantService.Rules("send the outreach", Facts, sales: false, null).Action?.Page);
    }

    [Fact]
    public void Model_cannot_propose_the_other_workspaces_flow()
    {
        Assert.Equal("outreach", AssistantService.Parse("""{"reply":"Shall I?","action":{"kind":"outreach"}}""", sales: true)?.Action?.Kind);
        Assert.Null(AssistantService.Parse("""{"reply":"Shall I?","action":{"kind":"outreach"}}""", sales: false)?.Action);
        Assert.Equal(3, AssistantService.Parse("""{"reply":"Shall I?","action":{"kind":"launch","query":"software engineer","location":"Gurugram","days":3}}""", false)?.Action?.Days);
    }

    [Fact]
    public void A_named_campaign_becomes_the_target_of_review()
    {
        var mern = new AssistantCampaign(Guid.NewGuid(), "MernStack developers in Gurugram — voice", 49, 1, 0, "Completed");
        var ds = new AssistantCampaign(Guid.NewGuid(), "Data Scientist in Gurugram — voice", 12, 3, 1, "Running");
        const string json = """{"reply":"It found 49 jobs. Shall I open them?","action":{"kind":"review","campaign":"mern stack"}}""";
        var r = AssistantService.WithCampaign(AssistantService.Parse(json, false), json, [ds, mern]);
        Assert.Equal(mern.Id, r?.Action?.CampaignId);
        Assert.Equal($"/opportunities?campaign={mern.Id}", r?.Action?.Page);
    }

    [Fact]
    public void What_next_points_at_the_most_useful_step()
    {
        Assert.Equal("/approvals", AssistantService.Rules("what should I do next", Facts, false, null).Action?.Page);
        var approvedOnly = new Dictionary<string, int>(Facts) { ["waitingForApproval"] = 0, ["approvedNotYetActedOn"] = 3 };
        Assert.Equal("outreach", AssistantService.Rules("what next", approvedOnly, sales: true, null).Action?.Kind);
        Assert.Equal("apply", AssistantService.Rules("what next", approvedOnly, sales: false, null).Action?.Kind);
    }
}
