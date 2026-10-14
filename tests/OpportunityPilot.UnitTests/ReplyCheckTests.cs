using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Mail;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>Reply detection on top of a fake inbox: what counts as a reply, what it changes, and that a second check changes nothing.</summary>
public class ReplyCheckTests
{
    private static readonly DateTime SentAt = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T0 = SentAt.AddDays(3);

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }

    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }

    private sealed class FakeReader : IMailboxReader
    {
        public MailboxReadResult Next { get; set; } = new([]);
        public int Calls { get; private set; }
        public DateTime? Since { get; private set; }
        public Task<MailboxReadResult> ReadHeadersSinceAsync(string credentialsJson, DateTime sinceUtc, CancellationToken ct)
        {
            Calls++;
            Since = sinceUtc;
            return Task.FromResult(Next);
        }
    }

    private static readonly MailOptions Mail = new() { TokenKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()) };

    private sealed record Setup(ReplyCheckService Service, InMemoryAppDbContext Db, FakeReader Reader, Guid Owner, Opportunity Opportunity, MailboxConnection Mailbox);

    private static async Task<Setup> Create(string smtpHost = "smtp.gmail.com", string recipient = "ops@northwind.example", bool send = true, OpportunityStatus? status = null)
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var opportunity = new Opportunity(owner, Guid.NewGuid(), OpportunityMode.Customer, "customer:northwind", SentAt);
        opportunity.ApplyResearch("Dynamics partner", "Northwind", null, null, null, null, null, null, 80, 80, FilterOutcome.Qualified, null, "[]", "[]", "[]", 0, Guid.NewGuid(), SentAt);
        opportunity.ChangeStatus(status ?? OpportunityStatus.Contacted, SentAt);
        var draft = new OutreachDraft(owner, opportunity.Id, DraftChannel.Email, recipient, true, "Dynamics 365 delivery", "Hello Northwind.", DraftSource.Template, "[]", SentAt);
        if (send) { draft.Approve(draft.Version, SentAt); draft.MarkSent(SentAt); }
        var protector = new TokenProtector(Options.Create(Mail));
        var mailbox = new MailboxConnection(owner, MailProvider.Smtp, "asha@gmail.test",
            protector.Protect(JsonSerializer.Serialize(new SmtpCredentials(smtpHost, 587, "asha@gmail.test", "app-password"))), SentAt);
        var followUp = new NextAction(owner, opportunity.Id, NextActionKind.FollowUp, "Follow up if there is no reply", SentAt.AddDays(5), "UTC", SentAt);
        var call = new NextAction(owner, opportunity.Id, NextActionKind.Call, "Call them", SentAt.AddDays(5), "UTC", SentAt);
        db.AddRange(opportunity, draft, mailbox, followUp, call);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reader = new FakeReader();
        var service = new ReplyCheckService(db, new Owner(owner), new Clock(T0), reader, protector, NullLogger<ReplyCheckService>.Instance);
        return new(service, db, reader, owner, opportunity, mailbox);
    }

    private static MailHeader Header(string from, DateTime at, string? subject = "Re: Dynamics 365 delivery", bool auto = false) =>
        new(from, subject, at, $"<{Guid.NewGuid():N}@northwind.example>", null, null, auto);

    [Fact]
    public async Task A_reply_from_the_recipient_is_recorded_moves_the_lead_to_responded_and_cancels_its_follow_up()
    {
        var s = await Create();
        var at = SentAt.AddHours(5);
        s.Reader.Next = new([Header("OPS@northwind.example", at), Header("someone@else.example", at)]);

        var result = await s.Service.CheckAsync(CancellationToken.None);

        Assert.Equal(2, result.Checked);
        Assert.Equal(1, result.Replies);
        var item = Assert.Single(result.Items);
        Assert.Equal((s.Opportunity.Id, "Northwind", "OPS@northwind.example", "Re: Dynamics 365 delivery", at), (item.OpportunityId, item.Organization, item.From, item.Subject, item.ReceivedAt));
        Assert.Equal(SentAt, s.Reader.Since);

        var activity = Assert.Single(await s.Db.Activities.Where(a => a.Kind == ActivityKinds.Replied).ToListAsync());
        Assert.Equal("Reply received from OPS@northwind.example on 2026-10-10 14:00:00 UTC: Re: Dynamics 365 delivery", activity.Detail);
        Assert.Equal(OpportunityStatus.Responded, (await s.Db.Opportunities.SingleAsync()).Status);
        var actions = await s.Db.NextActions.ToListAsync();
        Assert.Equal(NextActionState.Cancelled, actions.Single(a => a.Kind == NextActionKind.FollowUp).State);
        Assert.Equal(NextActionState.Open, actions.Single(a => a.Kind == NextActionKind.Call).State);
    }

    [Fact]
    public async Task Checking_twice_records_nothing_twice()
    {
        var s = await Create();
        s.Reader.Next = new([Header("ops@northwind.example", SentAt.AddHours(5))]);
        Assert.Equal(1, (await s.Service.CheckAsync(CancellationToken.None)).Replies);
        s.Db.ChangeTracker.Clear();

        var again = await s.Service.CheckAsync(CancellationToken.None);

        Assert.Equal((1, 0), (again.Checked, again.Replies));
        Assert.Empty(again.Items);
        Assert.Single(await s.Db.Activities.Where(a => a.Kind == ActivityKinds.Replied).ToListAsync());
        Assert.Single(await s.Db.Activities.Where(a => a.Kind == ActivityKinds.StatusChanged).ToListAsync());
    }

    [Fact]
    public async Task Mail_from_before_the_send_an_automatic_reply_or_the_person_themselves_is_not_a_reply()
    {
        var s = await Create();
        s.Reader.Next = new([
            Header("ops@northwind.example", SentAt.AddHours(-1)),
            Header("ops@northwind.example", SentAt.AddHours(2), "Automatic reply: Dynamics 365 delivery", auto: true),
            Header("asha@gmail.test", SentAt.AddHours(3)),
        ]);
        var result = await s.Service.CheckAsync(CancellationToken.None);
        Assert.Equal((3, 0), (result.Checked, result.Replies));
        Assert.Equal(OpportunityStatus.Contacted, (await s.Db.Opportunities.SingleAsync()).Status);
        Assert.Equal(NextActionState.Open, (await s.Db.NextActions.FirstAsync(a => a.Kind == NextActionKind.FollowUp)).State);
    }

    [Fact]
    public async Task A_lead_already_past_responded_keeps_its_status_but_the_reply_is_recorded()
    {
        var s = await Create(status: OpportunityStatus.Interested);
        s.Reader.Next = new([Header("ops@northwind.example", SentAt.AddHours(5))]);
        Assert.Equal(1, (await s.Service.CheckAsync(CancellationToken.None)).Replies);
        Assert.Equal(OpportunityStatus.Interested, (await s.Db.Opportunities.SingleAsync()).Status);
    }

    [Fact]
    public async Task Without_a_sent_email_the_inbox_is_not_read()
    {
        var s = await Create(send: false);
        var result = await s.Service.CheckAsync(CancellationToken.None);
        Assert.Equal((0, 0), (result.Checked, result.Replies));
        Assert.Equal(0, s.Reader.Calls);
    }

    [Fact]
    public async Task A_refused_login_asks_to_reconnect()
    {
        var s = await Create();
        s.Reader.Next = MailboxReadResult.Failed("Your email provider refused the login for reading replies. Check the app password.", loginRefused: true);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => s.Service.CheckAsync(CancellationToken.None));
        Assert.StartsWith("Your email provider refused the login", ex.Message);
        Assert.Equal(MailboxStatus.NeedsReconnect, (await s.Db.MailboxConnections.SingleAsync()).Status);
        // With the mailbox waiting to be reconnected, the next check says so before reading anything.
        s.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.CheckAsync(CancellationToken.None));
        Assert.Equal(1, s.Reader.Calls);
    }

    [Fact]
    public async Task A_refusal_from_microsoft_keeps_the_mailbox_connected_for_sending()
    {
        var s = await Create(smtpHost: "smtp-mail.outlook.com");
        s.Reader.Next = MailboxReadResult.Failed("Your email provider refused the login for reading replies. Microsoft has turned off…", loginRefused: true);
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.CheckAsync(CancellationToken.None));
        Assert.Equal(MailboxStatus.Connected, (await s.Db.MailboxConnections.SingleAsync()).Status);
    }

    [Fact]
    public async Task Without_a_connected_app_password_mailbox_the_check_says_what_to_do()
    {
        var s = await Create();
        s.Db.MailboxConnections.RemoveRange(s.Db.MailboxConnections);
        await s.Db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<ConflictException>(() => s.Service.CheckAsync(CancellationToken.None));
        Assert.Contains("Connect your email", ex.Message);
    }

    [Fact]
    public async Task The_background_pass_checks_people_with_a_recent_send_by_their_id_and_survives_a_refusal()
    {
        var s = await Create();
        s.Reader.Next = new([Header("ops@northwind.example", SentAt.AddHours(5))]);
        var (last, replies) = await s.Service.CheckDueOwnersAsync(null, CancellationToken.None);
        Assert.Null(last); // fewer than a full pass, so the next pass starts from the beginning
        Assert.Equal(1, replies);
        Assert.Equal(OpportunityStatus.Responded, (await s.Db.Opportunities.SingleAsync()).Status);

        s.Reader.Next = MailboxReadResult.Failed("Your email provider refused the login for reading replies.", loginRefused: true);
        Assert.Equal(0, (await s.Service.CheckDueOwnersAsync(null, CancellationToken.None)).Replies);
        Assert.Equal(MailboxStatus.NeedsReconnect, (await s.Db.MailboxConnections.SingleAsync()).Status);
        // A mailbox that needs reconnecting is not visited again.
        await s.Service.CheckDueOwnersAsync(null, CancellationToken.None);
        Assert.Equal(2, s.Reader.Calls);
    }
}
