using System.Net;

namespace OpportunityPilot.IntegrationTests;

/// <summary>The hiring-trends endpoint: needs sign-in, validates its inputs, and says plainly when the provider key is missing.</summary>
public class TrendsApiTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [Fact]
    public async Task Trends_require_sign_in_validate_input_and_report_a_missing_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/trends?query=dev")).StatusCode);

        var user = PostgresApiFactory.ClientFor(factory, "trends@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/trends?query=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/trends?query=dev&pages=9")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync("/api/v1/trends?query=dev&datePosted=yesterday")).StatusCode);

        var report = await user.GetJson("/api/v1/trends?query=react%20developer&location=Chicago&pages=2");
        Assert.Equal("NotConfigured", report.Str("status"));            // no JSearch key in tests
        Assert.Equal(0, report.Int("sampled"));
    }
}
