using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Capabilities;
using OpportunityPilot.Application.Configuration;

namespace OpportunityPilot.UnitTests;

public class CapabilityServiceTests
{
    private static CapabilitiesDto Get(FeatureOptions f, AiOptions a, AdzunaOptions? adzuna = null, SetupState? setup = null) =>
        new CapabilityService(Options.Create(f), Options.Create(a), Options.Create(new DatabaseOptions { Provider = "Postgres" }),
                Options.Create(adzuna ?? new AdzunaOptions()), Options.Create(new JsearchOptions()), setup ?? new SetupState())
            .Get("Test");

    [Fact]
    public void Sign_in_providers_are_listed_and_named_in_the_sign_in_capability()
    {
        var caps = Get(new FeatureOptions(), new AiOptions(), setup: new SetupState { SignInProviders = ["hanko", "supabase"] });

        Assert.Equal(["hanko", "supabase"], caps.SignInProviders);
        var auth = caps.Items.Single(i => i.Key == "auth");
        Assert.Equal(CapabilityStatus.Ready, auth.Status);
        Assert.Contains("Hanko", auth.Detail);
        Assert.Contains("Supabase", auth.Detail);
    }

    [Fact]
    public void Without_accounts_no_sign_in_provider_is_listed()
    {
        Assert.Empty(Get(new FeatureOptions(), new AiOptions()).SignInProviders);
    }

    [Theory]
    [InlineData("greenhouse", "Public job boards of companies that use Greenhouse; no key needed.")]
    [InlineData("lever", "Public job postings of companies that use Lever; no key needed.")]
    public void Keyless_job_board_sources_are_ready(string key, string detail)
    {
        var item = Get(new FeatureOptions(), new AiOptions()).Items.Single(i => i.Key == key);
        Assert.Equal(CapabilityStatus.Ready, item.Status);
        Assert.Equal("Sources", item.Category);
        Assert.Equal(detail, item.Detail);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("app-id", null)]
    [InlineData(null, "app-key")]
    [InlineData(" ", "app-key")]
    public void Adzuna_without_both_keys_is_not_configured(string? appId, string? appKey)
    {
        var item = Get(new FeatureOptions(), new AiOptions(), new AdzunaOptions { AppId = appId, AppKey = appKey }).Items.Single(i => i.Key == "adzuna");
        Assert.Equal(CapabilityStatus.NotConfigured, item.Status);
        Assert.Equal("Sources", item.Category);
    }

    [Fact]
    public void Adzuna_with_keys_is_ready_and_never_shows_them()
    {
        var caps = Get(new FeatureOptions(), new AiOptions(), new AdzunaOptions { AppId = "fake-app-id-123", AppKey = "fake-app-key-456" });

        var item = caps.Items.Single(i => i.Key == "adzuna");
        Assert.Equal(CapabilityStatus.Ready, item.Status);
        var everything = string.Join("|", caps.Items.SelectMany(i => new[] { i.Detail }.Concat(i.Can).Concat(i.Cannot)));
        Assert.DoesNotContain("fake-app-id-123", everything);
        Assert.DoesNotContain("fake-app-key-456", everything);
    }

    [Fact]
    public void Without_a_key_gemini_is_not_configured_and_rules_mode_applies()
    {
        var caps = Get(new FeatureOptions { GeminiEnabled = true }, new AiOptions { GeminiApiKey = null });

        Assert.Equal(CapabilityStatus.NotConfigured, caps.Items.Single(i => i.Key == "gemini").Status);
        Assert.Equal("Rules", caps.AiMode);
    }

    [Fact]
    public void A_key_alone_never_reports_gemini_as_ready()
    {
        var caps = Get(new FeatureOptions { GeminiEnabled = true }, new AiOptions { GeminiApiKey = "secret-value" });

        var gemini = caps.Items.Single(i => i.Key == "gemini");
        Assert.Equal(CapabilityStatus.Configured, gemini.Status);
        Assert.DoesNotContain("secret-value", gemini.Detail);
    }

    [Theory]
    [InlineData("linkedin", CapabilityStatus.LocalAgent)]
    [InlineData("naukri", CapabilityStatus.LocalAgent)]
    [InlineData("instahyre", CapabilityStatus.LocalAgent)]
    public void Job_platforms_report_how_applications_happen(string key, CapabilityStatus expected)
    {
        var caps = Get(new FeatureOptions(), new AiOptions());
        Assert.Equal(expected, caps.Items.Single(i => i.Key == key).Status);
    }

    [Theory]
    [InlineData("csv-import", "Import with preview and row-level errors.")]
    [InlineData("public-urls", "Safe fetch with private-address blocking; pages that need a login or JavaScript are marked for manual input.")]
    [InlineData("feeds", "Discovers only what a permitted RSS/Atom feed contains.")]
    [InlineData("rules", "Deterministic extraction and transparent scoring for Job and Customer campaigns.")]
    public void Research_capabilities_built_in_m2_and_m3_are_ready(string key, string detail)
    {
        var item = Get(new FeatureOptions(), new AiOptions()).Items.Single(i => i.Key == key);
        Assert.Equal(CapabilityStatus.Ready, item.Status);
        Assert.Equal(detail, item.Detail);
        Assert.NotEmpty(item.Can);
    }

    [Theory]
    [InlineData("gmail", CapabilityStatus.Disabled)]
    [InlineData("mongo-archive", CapabilityStatus.Disabled)]
    public void Capabilities_not_built_yet_are_not_reported_ready(string key, CapabilityStatus expected) =>
        Assert.Equal(expected, Get(new FeatureOptions(), new AiOptions()).Items.Single(i => i.Key == key).Status);

    [Fact]
    public void Scheduler_capability_is_reported_ready()
    {
        var item = Get(new FeatureOptions(), new AiOptions()).Items.Single(i => i.Key == "scheduler");
        Assert.Equal(CapabilityStatus.Ready, item.Status);
    }

    [Theory]
    [InlineData("linkedin")]
    [InlineData("naukri")]
    public void Agent_platforms_warn_that_automation_is_unofficial(string key)
    {
        var item = Get(new FeatureOptions(), new AiOptions()).Items.Single(i => i.Key == key);
        Assert.Contains("Unofficial automation", item.Detail);
    }

    [Fact]
    public void Job_boards_need_a_jsearch_key()
    {
        var item = Get(new FeatureOptions(), new AiOptions()).Items.Single(i => i.Key == "job-boards");
        Assert.Equal(CapabilityStatus.NotConfigured, item.Status);
        Assert.Contains("Jsearch__Key", item.Detail);
    }
}
