using OpportunityPilot.Application.Drafts;

namespace OpportunityPilot.UnitTests;

public sealed class OutreachTemplateTests
{
    [Fact]
    public void A_posting_sentence_is_not_used_as_the_company_name()
    {
        const string title = "Comviva is hiring a Senior Software Engineer in Gurugram. Java and Kafka.";
        Assert.Null(DraftService.CleanOrganization(title, title));
        Assert.Null(DraftService.CleanOrganization("", "Engineer"));
        Assert.Equal("Comviva", DraftService.CleanOrganization("Comviva.", "Senior Software Engineer"));
    }

    [Fact]
    public void The_role_is_kept_short_for_the_subject_and_the_sentence()
    {
        Assert.Equal("Senior Software Engineer in Gurugram", DraftService.ShortTitle("Comviva is hiring a Senior Software Engineer in Gurugram. Java and Kafka."));
        Assert.True(DraftService.ShortTitle(new string('a', 30) + " " + new string('b', 90)).Length <= 80);
        Assert.Equal("Senior .NET Developer", DraftService.ShortTitle("Senior .NET Developer."));
    }

    [Fact]
    public void A_company_lead_is_told_apart_from_a_posting_sentence()
    {
        Assert.True(DraftService.IsCompanyLead("Conscia Sverige", "Conscia Sverige"));
        Assert.False(DraftService.IsCompanyLead("Comviva is hiring a Senior Software Engineer in Gurugram. Java and Kafka.", "Comviva is hiring a Senior Software Engineer in Gurugram. Java and Kafka."));
        Assert.False(DraftService.IsCompanyLead("Senior .NET Developer", "Acme"));
        Assert.False(DraftService.IsCompanyLead("Acme", null));
    }
}
