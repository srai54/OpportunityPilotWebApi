using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.UnitTests;

public sealed class CampaignAutoApproveTests
{
    /// <summary>Regression (2026-10-19): a Sales campaign with auto-approve threw "Only Job campaigns can auto-approve" (500 on create).</summary>
    [Theory]
    [InlineData(OpportunityMode.Job)]
    [InlineData(OpportunityMode.Customer)]
    public void Job_and_sales_campaigns_can_auto_approve(OpportunityMode mode)
    {
        var c = NewCampaign(mode);
        c.SetAutoApprove(50);
        Assert.Equal(50, c.AutoApproveMinScore);
        c.SetAutoApprove(null);
        Assert.Null(c.AutoApproveMinScore);
        Assert.Throws<ArgumentOutOfRangeException>(() => c.SetAutoApprove(101));
    }

    private static Campaign NewCampaign(OpportunityMode mode) =>
        new(Guid.NewGuid(), Guid.NewGuid(), mode, "C", null, "{}", "{}", 25, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc));
}
