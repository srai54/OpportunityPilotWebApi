using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Outreach;
using OpportunityPilot.Domain.Circle;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>LI1: what the agent may send on LinkedIn (only exact approved text to a profile link) and that each approval is sent at most once.</summary>
public class LinkedInOutboxTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private const string Asha = "https://www.linkedin.com/in/asha-rao";
    private const string Note = "Hi Asha, I saw Northwind is hiring a Senior .NET Engineer. I build .NET services. I'd like to connect. Ravi";

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }

    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }

    private sealed record Setup(InMemoryAppDbContext Db, Guid Owner)
    {
        public LinkedInOutboxService For(Guid owner)
        {
            var user = new Owner(owner);
            var clock = new Clock(T0.AddHours(1));
            return new LinkedInOutboxService(Db, user, clock, new ProviderGateway(Db, user, clock));
        }
        public LinkedInOutboxService Service => For(Owner);
    }

    private static async Task<(Setup Setup, Dictionary<string, OutreachDraft> Drafts)> Create()
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var drafts = new Dictionary<string, OutreachDraft>();

        OutreachDraft Add(string name, Guid who, DraftChannel channel, string? recipient, bool approve, string body = Note)
        {
            var o = new Opportunity(who, Guid.NewGuid(), OpportunityMode.Job, $"job:{name}", T0);
            o.ApplyResearch("Senior .NET Engineer", "Northwind", null, null, null, JobPlatform.LinkedIn, name, null, 80, 80, FilterOutcome.Qualified,
                null, "[]", "[]", "[]", 0, Guid.NewGuid(), T0);
            var d = new OutreachDraft(who, o.Id, channel, recipient, false, null, body, DraftSource.Template, "[]", T0);
            if (approve) d.Approve(d.Version, T0);
            db.AddRange(o, d);
            drafts[name] = d;
            return d;
        }

        Add("ready", owner, DraftChannel.LinkedInMessage, Asha, approve: true);
        db.CirclePeople.Add(new CirclePerson(owner, drafts["ready"].OpportunityId, "Asha Rao", "Recruiter", Asha, CircleRelation.Recruiter, 2, CircleSource.LinkedInAgent, T0));
        Add("long", owner, DraftChannel.LinkedInMessage, "https://www.linkedin.com/in/long", approve: true, body: new string('x', 400));
        Add("unapproved", owner, DraftChannel.LinkedInMessage, "https://www.linkedin.com/in/u", approve: false);
        var edited = Add("edited", owner, DraftChannel.LinkedInMessage, "https://www.linkedin.com/in/e", approve: true);
        edited.Update("https://www.linkedin.com/in/e", false, null, Note + " (edited)", T0);   // edit after approval clears it
        Add("byName", owner, DraftChannel.LinkedInMessage, "Ms Rao", approve: true);
        Add("email", owner, DraftChannel.Email, "ops@northwind.example", approve: true);
        Add("suppressed", owner, DraftChannel.LinkedInMessage, "https://www.linkedin.com/in/blocked", approve: true);
        db.Suppressions.Add(new Suppression(owner, "https://www.linkedin.com/in/blocked", "asked", T0));
        Add("foreign", other, DraftChannel.LinkedInMessage, "https://www.linkedin.com/in/f", approve: true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (new Setup(db, owner), drafts);
    }

    [Fact]
    public async Task The_outbox_holds_only_approved_unsent_LinkedIn_messages_to_a_profile_link_of_this_owner()
    {
        var (s, d) = await Create();
        var items = await s.Service.ListAsync(default);

        Assert.Equal(new[] { d["ready"].Id, d["long"].Id }.Order(), items.Select(i => i.DraftId).Order());
        var ready = items.Single(i => i.DraftId == d["ready"].Id);
        Assert.Equal((Asha, Note, d["ready"].Version, true, "Asha Rao", "Northwind"), (ready.ProfileUrl, ready.Body, ready.Version, ready.FitsConnectionNote, ready.PersonName, ready.Organization));
        Assert.False(items.Single(i => i.DraftId == d["long"].Id).FitsConnectionNote);
        Assert.Equal(new[] { d["foreign"].Id }, (await s.For(d["foreign"].OwnerId).ListAsync(default)).Select(i => i.DraftId));
    }

    [Fact]
    public async Task A_send_starts_once_leaves_the_outbox_and_is_confirmed_once()
    {
        var (s, d) = await Create();
        var id = d["ready"].Id;
        var started = await s.Service.StartAsync(id, new StartExecutionRequest(d["ready"].Version), default);
        Assert.Equal((ExecutionProvider.LinkedInAgent, ExecutionState.AwaitingManualConfirmation, false), (started.Provider, started.State, started.Reused));

        var again = await s.Service.StartAsync(id, new StartExecutionRequest(d["ready"].Version), default);
        Assert.Equal((started.Id, true), (again.Id, again.Reused));   // the agent must not send a reused one again
        Assert.DoesNotContain(await s.Service.ListAsync(default), i => i.DraftId == id);

        var done = await s.Service.ConfirmAsync(id, new AgentLinkedInConfirmRequest("Connection request with note sent", "connect"), default);
        Assert.Equal(ExecutionState.Succeeded, done.State);
        Assert.Equal(DraftState.Sent, (await s.Db.OutreachDrafts.AsNoTracking().SingleAsync(x => x.Id == id)).State);
        Assert.Contains("desktop agent", (await s.Db.Activities.AsNoTracking().SingleAsync(a => a.OpportunityId == d["ready"].OpportunityId && a.Detail.Contains("sent"))).Detail);
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.ConfirmAsync(id, new AgentLinkedInConfirmRequest("twice"), default));
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.FailAsync(id, new FailExecutionRequest("late"), default));
        Assert.Single(await s.Db.ProviderExecutions.ToListAsync());
    }

    [Fact]
    public async Task A_failed_send_returns_to_the_outbox_for_a_new_attempt()
    {
        var (s, d) = await Create();
        var id = d["ready"].Id;
        var first = await s.Service.StartAsync(id, new StartExecutionRequest(d["ready"].Version), default);
        var failed = await s.Service.FailAsync(id, new FailExecutionRequest("LinkedIn showed a security check"), default);
        Assert.Equal(ExecutionState.Failed, failed.State);
        Assert.Contains(await s.Service.ListAsync(default), i => i.DraftId == id);
        var retry = await s.Service.StartAsync(id, new StartExecutionRequest(d["ready"].Version), default);
        Assert.NotEqual(first.Id, retry.Id);
    }

    [Fact]
    public async Task Starting_needs_the_exact_approved_version_a_profile_link_and_this_owner()
    {
        var (s, d) = await Create();
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.StartAsync(d["ready"].Id, new StartExecutionRequest(d["ready"].Version + 1), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.StartAsync(d["unapproved"].Id, new StartExecutionRequest(d["unapproved"].Version), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.StartAsync(d["byName"].Id, new StartExecutionRequest(d["byName"].Version), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.StartAsync(d["email"].Id, new StartExecutionRequest(d["email"].Version), default));
        var blocked = await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.StartAsync(d["suppressed"].Id, new StartExecutionRequest(d["suppressed"].Version), default));
        Assert.Contains("recipient", blocked.Errors.Keys);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.StartAsync(d["foreign"].Id, new StartExecutionRequest(d["foreign"].Version), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.ConfirmAsync(d["foreign"].Id, new AgentLinkedInConfirmRequest("x"), default));
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.ConfirmAsync(d["ready"].Id, new AgentLinkedInConfirmRequest("never started"), default));
        Assert.Empty(await s.Db.ProviderExecutions.ToListAsync());
    }
}
