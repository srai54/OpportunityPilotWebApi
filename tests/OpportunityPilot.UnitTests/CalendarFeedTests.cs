using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Interviews;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>The private .ics feed: the candidate's own planned interview rounds appear next to follow-ups and sales interviews.</summary>
public class CalendarFeedTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly MailOptions Mail = new() { TokenKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()) };

    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }

    private sealed class NoHttp : IConnectorHttp
    {
        public Task<ConnectorResponse> SendAsync(ConnectorRequest request, CancellationToken ct) => throw new InvalidOperationException("No HTTP in this test.");
    }

    private static Opportunity Applied(Guid owner, string organization)
    {
        var o = new Opportunity(owner, Guid.NewGuid(), OpportunityMode.Job, Guid.NewGuid().ToString("N"), T0);
        o.ApplyResearch("Software Engineer", organization, "Gurugram", null, null, JobPlatform.LinkedIn, "1", null, 80, 80, FilterOutcome.Qualified,
            null, "[]", "[]", "[]", 0, Guid.NewGuid(), T0);
        return o;
    }

    [Fact]
    public async Task Scheduled_job_interviews_with_a_time_are_in_the_feed_and_the_rest_are_not()
    {
        var db = new InMemoryAppDbContext(
            new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var protector = new TokenProtector(Options.Create(Mail));
        var owner = Guid.NewGuid();
        var feed = new IntegrationConnection(owner, IntegrationKind.CalendarFeed, "Calendar", protector.Protect("feed-key"), "{}", "", T0);
        var acme = Applied(owner, "Acme Labs");
        var other = Applied(Guid.NewGuid(), "Someone Else Ltd");
        var planned = new JobInterview(owner, acme.Id, 2, T0.AddDays(1), "https://meet.example.com/abc", null, T0);
        var noTime = new JobInterview(owner, acme.Id, 3, null, null, null, T0);
        var cancelled = new JobInterview(owner, acme.Id, 4, T0.AddDays(2), null, null, T0);
        cancelled.SetStatus(JobInterviewStatus.Cancelled, T0);
        var stranger = new JobInterview(other.OwnerId, other.Id, 1, T0.AddDays(1), null, null, T0);
        db.AddRange(feed, acme, other, planned, noTime, cancelled, stranger);
        await db.SaveChangesAsync();

        var notifier = new IntegrationNotifier(db, new Clock(T0), protector, new NoHttp(), Options.Create(new IntegrationOptions()),
            Options.Create(Mail), NullLogger<IntegrationNotifier>.Instance);
        var ics = await notifier.CalendarAsync(feed.Id, "feed-key", default);

        Assert.NotNull(ics);
        Assert.Contains($"UID:job-interview-{planned.Id:N}@opportunitypilot", ics);
        Assert.Contains("SUMMARY:Interview R2 · Acme Labs", ics);
        Assert.Contains("DESCRIPTION:https://meet.example.com/abc", ics);
        Assert.Contains("DTSTART:20261016T090000Z", ics);
        Assert.Contains("DTEND:20261016T100000Z", ics);
        Assert.DoesNotContain(noTime.Id.ToString("N"), ics);
        Assert.DoesNotContain(cancelled.Id.ToString("N"), ics);
        Assert.DoesNotContain(stranger.Id.ToString("N"), ics);
        Assert.Null(await notifier.CalendarAsync(feed.Id, "wrong-key", default));
    }
}
