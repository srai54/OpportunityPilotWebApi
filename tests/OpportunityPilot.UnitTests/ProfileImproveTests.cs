using OpportunityPilot.Application.Profiles;

namespace OpportunityPilot.UnitTests;

public sealed class ProfileImproveTests
{
    private static readonly Dictionary<string, string> Fields = new() { ["skills"] = "Python, SQL,python ; React", ["offer"] = "Data   scientist" };

    [Fact]
    public void Without_ai_skills_become_one_clean_list_and_spacing_is_tidied()
    {
        var s = ProfileImproveService.Tidy(Fields);
        Assert.Contains(s, x => x.Key == "skills" && x.Text == "Python, SQL, React");
        Assert.Contains(s, x => x.Key == "offer" && x.Text == "Data scientist");
    }

    [Fact]
    public void Model_suggestions_are_kept_only_for_sent_fields_and_real_changes()
    {
        const string json = """{"suggestions":[{"key":"offer","text":"Data scientist (Python, SQL)","why":"More specific"},{"key":"salary","text":"x"},{"key":"skills","text":"Python, SQL,python ; React"}]}""";
        var s = ProfileImproveService.Parse(json, Fields);
        Assert.NotNull(s);
        Assert.Single(s!);
        Assert.Equal("offer", s![0].Key);
        Assert.Null(ProfileImproveService.Parse("nope", Fields));
    }
}
