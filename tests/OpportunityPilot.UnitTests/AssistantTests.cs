using OpportunityPilot.Application.Assistant;

namespace OpportunityPilot.UnitTests;

public sealed class AssistantTests
{
    private static readonly Dictionary<string, int> Facts = new()
    {
        ["jobsFoundThisWeek"] = 12, ["goodMatches"] = 4, ["waitingForApproval"] = 3, ["approvedNotYetActedOn"] = 1,
        ["messagesToCheck"] = 2, ["applied"] = 5, ["replies"] = 1, ["agentNeedsYou"] = 0,
    };

    [Fact]
    public void Without_ai_a_status_question_gets_the_summary_from_real_numbers()
    {
        var r = AssistantService.Rules("what is my status", Facts, sales: false, "NoKey");
        Assert.Equal("This week 12 jobs found, 4 good matches, 3 waiting for your approval, 2 messages to check, 5 applied and 1 reply.", r.Reply);
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
}
