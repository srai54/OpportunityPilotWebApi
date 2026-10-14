using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Agents;
using OpportunityPilot.Application.Circle;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Agents;
using OpportunityPilot.Domain.Circle;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>JDC1: the people around a lead — profile links, the request/ready cycle, owner scoping and the agent's upsert.</summary>
public class CircleTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    [Theory]
    [InlineData("https://www.linkedin.com/in/asha-rao", "https://www.linkedin.com/in/asha-rao")]
    [InlineData("http://linkedin.com/in/asha-rao/", "https://www.linkedin.com/in/asha-rao")]
    [InlineData("https://in.linkedin.com/in/asha-rao?miniProfileUrn=urn%3Ali&trk=x#about", "https://www.linkedin.com/in/asha-rao")]
    [InlineData("www.linkedin.com/in/asha-rao/details/experience/", "https://www.linkedin.com/in/asha-rao")]
    [InlineData("  https://WWW.LinkedIn.com/IN/ACoAABcdEf123  ", "https://www.linkedin.com/in/ACoAABcdEf123")]
    [InlineData("https://www.linkedin.com/in/josé-garcía", "https://www.linkedin.com/in/jos%C3%A9-garc%C3%ADa")]
    public void Profile_links_are_stored_in_one_normal_form_and_keep_the_slug_case(string input, string expected)
    {
        Assert.True(LinkedInProfileUrl.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.True(LinkedInProfileUrl.IsNormalized(normalized));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Asha Rao")]
    [InlineData("https://www.linkedin.com/company/northwind")]
    [InlineData("https://www.linkedin.com/in/")]
    [InlineData("https://linkedin.com.evil.example/in/asha")]
    [InlineData("https://evil.example/in/asha")]
    [InlineData("ftp://www.linkedin.com/in/asha")]
    [InlineData("https://user@www.linkedin.com/in/asha")]
    [InlineData("https://www.linkedin.com:8443/in/asha")]
    [InlineData("https://www.linkedin.com/in/asha<script>")]
    public void Anything_but_a_LinkedIn_member_link_is_refused(string? input) =>
        Assert.False(LinkedInProfileUrl.TryNormalize(input, out _));

    [Fact]
    public void A_person_needs_a_name_a_profile_link_and_a_known_degree()
    {
        var owner = Guid.NewGuid();
        var opp = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new CirclePerson(owner, opp, " ", null, "https://www.linkedin.com/in/a", CircleRelation.Team, null, CircleSource.Manual, T0));
        Assert.Throws<ArgumentException>(() => new CirclePerson(owner, opp, "A", null, "https://example.com/in/a", CircleRelation.Team, null, CircleSource.Manual, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CirclePerson(owner, opp, "A", null, "https://www.linkedin.com/in/a", CircleRelation.Team, 4, CircleSource.Manual, T0));
        var p = new CirclePerson(owner, opp, " Asha ", " Recruiter at Northwind ", "linkedin.com/in/a/", CircleRelation.Recruiter, 2, CircleSource.LinkedInAgent, T0);
        Assert.Equal(("Asha", "Recruiter at Northwind", "https://www.linkedin.com/in/a", 2), (p.Name, p.Headline, p.ProfileUrl, p.Degree));
    }

    [Fact]
    public void A_later_search_refreshes_a_person_but_keeps_a_relation_chosen_by_hand()
    {
        var manual = new CirclePerson(Guid.NewGuid(), Guid.NewGuid(), "Asha", null, "https://www.linkedin.com/in/a", CircleRelation.HiringManager, null, CircleSource.Manual, T0);
        manual.Refresh("Asha Rao", "Engineering Manager", CircleRelation.Team, 2, T0.AddDays(1));
        Assert.Equal((CircleRelation.HiringManager, "Asha Rao", "Engineering Manager", 2), (manual.Relation, manual.Name, manual.Headline, manual.Degree));

        var found = new CirclePerson(Guid.NewGuid(), Guid.NewGuid(), "Ravi", "Recruiter", "https://www.linkedin.com/in/r", CircleRelation.Recruiter, 3, CircleSource.LinkedInAgent, T0);
        found.Refresh("Ravi", null, CircleRelation.Other, null, T0.AddDays(1));
        Assert.Equal((CircleRelation.Other, "Recruiter", 3), (found.Relation, found.Headline, found.Degree));   // missing values keep what was known
    }

    [Fact]
    public void A_request_waits_until_the_agent_delivers_and_asking_again_while_waiting_keeps_the_first_time()
    {
        var o = new Opportunity(Guid.NewGuid(), Guid.NewGuid(), OpportunityMode.Job, "job:1", T0);
        var version = o.Version;
        Assert.False(o.CircleWaiting);
        o.RequestCircle(T0);
        o.RequestCircle(T0.AddHours(1));
        Assert.Equal(T0, o.CircleRequestedAt);
        Assert.True(o.CircleWaiting);
        o.MarkCircleMapped(T0.AddHours(2));
        Assert.False(o.CircleWaiting);
        o.RequestCircle(T0.AddDays(1));
        Assert.True(o.CircleWaiting);
        Assert.Equal(version, o.Version);   // not a pipeline change
    }

    [Fact]
    public void The_People_scope_is_part_of_every_scope_and_can_be_named()
    {
        Assert.True(AgentKeyScope.All.HasFlag(AgentKeyScope.People));
        Assert.Equal(15, (int)AgentKeyScope.All);
        Assert.Equal(new[] { "Research", "Shortlist", "Applications", "People" }, AgentKeyService.ScopeNames(AgentKeyScope.All));
        Assert.Equal(new[] { "People" }, AgentKeyService.ScopeNames(AgentKeyScope.People));
    }

    private sealed record Setup(InMemoryAppDbContext Db, Clock Clock, Guid Owner, Opportunity Opportunity, Opportunity Foreign)
    {
        public CircleService For(Guid owner) => new(Db, new Owner(owner), Clock);
        public CircleService Service => For(Owner);
    }

    private static async Task<Setup> Create(string organization = "Northwind")
    {
        var db = new InMemoryAppDbContext(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        Opportunity Lead(Guid who, string key)
        {
            var o = new Opportunity(who, Guid.NewGuid(), OpportunityMode.Job, key, T0);
            o.ApplyResearch("Senior .NET Engineer", organization, "Pune", null, null, JobPlatform.LinkedIn, "1", null, 80, 80, FilterOutcome.Qualified,
                null, "[]", "[]", "[]", 0, Guid.NewGuid(), T0);
            return o;
        }
        var mine = Lead(owner, "job:mine");
        var foreign = Lead(Guid.NewGuid(), "job:foreign");
        db.AddRange(mine, foreign);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Setup(db, new Clock(T0), owner, mine, foreign);
    }

    private static AgentCirclePerson Found(string slug, CircleRelation relation = CircleRelation.Recruiter, string name = "Asha Rao", int? degree = 2) =>
        new(name, "Talent Acquisition at Northwind", $"https://www.linkedin.com/in/{slug}", relation, degree);

    [Fact]
    public async Task Requested_leads_reach_the_agent_and_a_delivery_makes_the_circle_ready()
    {
        var s = await Create();
        Assert.Equal("NotRequested", (await s.Service.GetAsync(s.Opportunity.Id, default)).Status);
        Assert.Empty(await s.Service.PendingForAgentAsync(default));

        var waiting = await s.Service.RequestAsync(s.Opportunity.Id, default);
        Assert.Equal(("Waiting", T0), (waiting.Status, waiting.RequestedAt));
        var request = Assert.Single(await s.Service.PendingForAgentAsync(default));
        Assert.Equal((s.Opportunity.Id, "Northwind", "Senior .NET Engineer", "Pune"), (request.OpportunityId, request.Organization, request.Title, request.Location));
        Assert.Empty(await s.For(s.Foreign.OwnerId).PendingForAgentAsync(default));   // owner-scoped

        s.Clock.Now = T0.AddMinutes(30);
        var result = await s.Service.DeliverAsync(new AgentCircleRequest(s.Opportunity.Id,
            [Found("asha"), Found("ASHA", name: "Asha R."), Found("ravi", CircleRelation.HiringManager, "Ravi Menon", 3)]), default);
        Assert.Equal((2, 2, 0, 2), (result.Accepted, result.Added, result.Updated, result.Total));   // the same link twice counts once

        var ready = await s.Service.GetAsync(s.Opportunity.Id, default);
        Assert.Equal("Ready", ready.Status);
        Assert.Equal(new[] { "Asha R.", "Ravi Menon" }, ready.People.Select(p => p.Name));   // recruiters first
        Assert.All(ready.People, p => Assert.Equal(CircleSource.LinkedInAgent, p.Source));
        Assert.Empty(await s.Service.PendingForAgentAsync(default));
        Assert.Single(await s.Db.Activities.Where(a => a.OpportunityId == s.Opportunity.Id).ToListAsync());

        // Delivering again updates the same rows instead of adding more.
        s.Db.ChangeTracker.Clear();
        var again = await s.Service.DeliverAsync(new AgentCircleRequest(s.Opportunity.Id, [Found("asha", degree: 1)]), default);
        Assert.Equal((1, 0, 1, 2), (again.Accepted, again.Added, again.Updated, again.Total));
        Assert.Equal(1, (await s.Db.CirclePeople.SingleAsync(p => p.Name == "Asha Rao")).Degree);
    }

    [Fact]
    public async Task An_empty_delivery_still_counts_as_mapped()
    {
        var s = await Create();
        await s.Service.RequestAsync(s.Opportunity.Id, default);
        s.Clock.Now = T0.AddMinutes(5);
        var result = await s.Service.DeliverAsync(new AgentCircleRequest(s.Opportunity.Id, []), default);
        Assert.Equal(0, result.Total);
        Assert.Equal("Ready", (await s.Service.GetAsync(s.Opportunity.Id, default)).Status);
    }

    [Fact]
    public async Task Another_owners_lead_is_not_found_for_any_circle_action()
    {
        var s = await Create();
        var id = s.Foreign.Id;
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.GetAsync(id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.RequestAsync(id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.AddAsync(id, new("Asha", "https://www.linkedin.com/in/asha", CircleRelation.Team), default));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.DeliverAsync(new AgentCircleRequest(id, [Found("asha")]), default));

        var theirs = await s.For(s.Foreign.OwnerId).AddAsync(id, new("Asha", "https://www.linkedin.com/in/asha", CircleRelation.Team), default);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.DeleteAsync(id, theirs.Id, default));
        Assert.Equal(1, await s.Db.CirclePeople.CountAsync());
    }

    [Fact]
    public async Task People_added_by_hand_are_checked_deduplicated_and_removable()
    {
        var s = await Create();
        var added = await s.Service.AddAsync(s.Opportunity.Id, new("Asha", "linkedin.com/in/asha/", CircleRelation.HiringManager, "Head of Engineering"), default);
        Assert.Equal(("https://www.linkedin.com/in/asha", CircleSource.Manual), (added.ProfileUrl, added.Source));
        await Assert.ThrowsAsync<ConflictException>(() => s.Service.AddAsync(s.Opportunity.Id, new("Asha", "https://www.linkedin.com/in/Asha", CircleRelation.Team), default));
        var invalid = await Assert.ThrowsAsync<RequestValidationException>(() =>
            s.Service.AddAsync(s.Opportunity.Id, new("", "https://example.com/asha", (CircleRelation)99, null, 7), default));
        Assert.Equal(new[] { "degree", "name", "profileUrl", "relation" }, invalid.Errors.Keys.Order());

        // The agent finding the same person later keeps the relation the person chose.
        await s.Service.DeliverAsync(new AgentCircleRequest(s.Opportunity.Id, [Found("asha", CircleRelation.Recruiter)]), default);
        Assert.Equal(CircleRelation.HiringManager, (await s.Service.GetAsync(s.Opportunity.Id, default)).People.Single().Relation);

        await s.Service.DeleteAsync(s.Opportunity.Id, added.Id, default);
        Assert.Empty((await s.Service.GetAsync(s.Opportunity.Id, default)).People);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Service.DeleteAsync(s.Opportunity.Id, added.Id, default));
    }

    [Fact]
    public async Task Deliveries_are_bounded_and_a_lead_without_a_company_cannot_be_requested()
    {
        var s = await Create();
        var tooMany = Enumerable.Range(0, CircleService.MaxPeoplePerDelivery + 1).Select(i => Found($"p{i}")).ToList();
        var e = await Assert.ThrowsAsync<RequestValidationException>(() => s.Service.DeliverAsync(new AgentCircleRequest(s.Opportunity.Id, tooMany), default));
        Assert.Contains("people", e.Errors.Keys);
        var bad = await Assert.ThrowsAsync<RequestValidationException>(() =>
            s.Service.DeliverAsync(new AgentCircleRequest(s.Opportunity.Id, [Found("ok"), new("X", null, "https://www.linkedin.com/company/x", CircleRelation.Team, null)]), default));
        Assert.Contains("people[1].profileUrl", bad.Errors.Keys);
        Assert.Empty(await s.Db.CirclePeople.ToListAsync());   // nothing half-saved

        var nameless = await Create(organization: "");
        await Assert.ThrowsAsync<RequestValidationException>(() => nameless.Service.RequestAsync(nameless.Opportunity.Id, default));
    }
}
