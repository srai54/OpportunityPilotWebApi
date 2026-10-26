using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Access;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Feedback;
using OpportunityPilot.Domain.Feedback;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>In-app feedback and browser error reports (2026-10-26): anyone sends, only the admin reads; errors are counted, kept 30 days.</summary>
public class FeedbackTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Caller(string? email, bool guest = false) : ICallerInfo { public string? Email => email; public bool IsGuest => guest; }
    private sealed class Clock(DateTime now) : TimeProvider { public DateTime Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => new(Now); }

    private static InMemoryAppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FeedbackService Service(InMemoryAppDbContext db, Guid owner, Clock clock, string? adminId = null)
    {
        var caller = new Caller("asha@example.com");
        var access = new AccessService(db, new Owner(owner), caller, Options.Create(new AccessOptions { AdminOwnerIds = adminId }), clock);
        return new FeedbackService(db, new Owner(owner), caller, access, clock, NullLogger<FeedbackService>.Instance);
    }

    [Fact]
    public async Task Feedback_is_validated_and_stored_with_the_page()
    {
        var db = NewDb();
        var s = Service(db, Guid.NewGuid(), new Clock(T0));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.SendAsync(new("Rant", null, "hello there", null, null), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.SendAsync(new("Bug", null, "x", null, null), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.SendAsync(new("Idea", 9, "A real idea", null, null), default));
        var dto = await s.SendAsync(new("idea", 5, "  Add a dark calendar  ", "/settings", "1.2.3"), default);
        Assert.Equal(("Idea", 5, "Add a dark calendar", "/settings", "New", "asha@example.com"), (dto.Kind, dto.Rating, dto.Message, dto.Page, dto.Status, dto.Email));
    }

    [Fact]
    public async Task Only_the_admin_reads_feedback_and_errors()
    {
        var db = NewDb();
        var admin = Guid.NewGuid();
        await Service(db, Guid.NewGuid(), new Clock(T0)).SendAsync(new("Bug", 2, "The chart is empty", "/", null), default);
        var someone = Service(db, Guid.NewGuid(), new Clock(T0), adminId: admin.ToString());
        await Assert.ThrowsAsync<ForbiddenException>(() => someone.ListAsync(default));
        await Assert.ThrowsAsync<ForbiddenException>(() => someone.ErrorsAsync(default));
        await Assert.ThrowsAsync<ForbiddenException>(() => someone.SummaryAsync(default));

        var boss = Service(db, admin, new Clock(T0.AddHours(1)), adminId: admin.ToString());
        var list = await boss.ListAsync(default);
        var done = await boss.SetStatusAsync(list[0].Id, new("Done"), default);
        Assert.Equal(("Done", T0.AddHours(1)), (done.Status, done.HandledAt));
        var summary = await boss.SummaryAsync(default);
        Assert.Equal((0, 1, 2.0, 1), (summary.New, summary.Last7Days, summary.AverageRating, summary.ByKind["Bug"]));
    }

    [Fact]
    public async Task The_same_error_on_the_same_page_is_counted_within_an_hour_and_stored_again_later()
    {
        var db = NewDb();
        var clock = new Clock(T0);
        var s = Service(db, Guid.NewGuid(), clock);
        await Assert.ThrowsAsync<RequestValidationException>(() => s.ReportErrorAsync(new(" ", null, null, null, null, null, null), null, default));
        var e = new ClientErrorRequest("TypeError: x is undefined", "window.error", "/dashboard", "at f (app.js:1)", "Edge", "1.0", "abc");
        await s.ReportErrorAsync(e, null, default);
        clock.Now = T0.AddMinutes(30);
        await s.ReportErrorAsync(e, null, default);
        var row = await db.ClientErrors.SingleAsync();
        Assert.Equal((2, T0, T0.AddMinutes(30)), (row.Count, row.FirstSeenAt, row.LastSeenAt));
        clock.Now = T0.AddHours(3);
        await s.ReportErrorAsync(e, null, default);
        Assert.Equal(2, await db.ClientErrors.CountAsync());
    }

    [Fact]
    public async Task Server_errors_join_the_same_list_with_the_type_and_route_only()
    {
        var db = NewDb();
        await FeedbackService.RecordServerErrorAsync(db, "InvalidOperationException", "POST", "/api/v1/staffing/deals/{id}", "corr1", "1.0", T0, default);
        await FeedbackService.RecordServerErrorAsync(db, "InvalidOperationException", "POST", "/api/v1/staffing/deals/{id}", "corr2", "1.0", T0.AddMinutes(5), default);
        var row = await db.ClientErrors.SingleAsync();
        Assert.Equal(("InvalidOperationException on POST", "server", "/api/v1/staffing/deals/{id}", 2, "corr1"), (row.Message, row.Source, row.Page, row.Count, row.CorrelationId));
        Assert.Null(row.Stack);
    }

    [Fact]
    public async Task Retention_removes_errors_not_seen_for_30_days()
    {
        var db = NewDb();
        db.ClientErrors.Add(new ClientError(null, "old", null, "/", null, null, null, null, T0.AddDays(-40)));
        db.ClientErrors.Add(new ClientError(null, "new", null, "/", null, null, null, null, T0.AddDays(-2)));
        await db.SaveChangesAsync();
        var result = await new RetentionService(db, new Clock(T0)).RunOnceAsync(default);
        Assert.Equal(1, result.ClientErrors);
        Assert.Equal("new", (await db.ClientErrors.SingleAsync()).Message);
    }
}
