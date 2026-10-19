using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Interviews;
using OpportunityPilot.Application.Profiles;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.SampleData;
using OpportunityPilot.Application.Sources;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Interviews;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Profiles;
using OpportunityPilot.Domain.Staffing;
using OpportunityPilot.Infrastructure.Persistence;
using OpportunityPilot.Infrastructure.Persistence.Repositories;

namespace OpportunityPilot.UnitTests;

/// <summary>Sample data: realistic, marked, owner-scoped, created once, built through the normal services so their rules hold.</summary>
public class SampleDataTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }

    private sealed class NoFetch : IWebFetcher
    {
        public string? CheckUrl(string url) => null;
        public Task<FetchResult> FetchAsync(string url, CancellationToken ct, int? maxBytes = null) => throw new InvalidOperationException("No network.");
        public Task<FetchResult> FetchJsonAsync(string url, CancellationToken ct, int? maxBytes = null) => throw new InvalidOperationException("No network.");
        public Task<FetchResult> PostJsonAsync(string url, string jsonBody, CancellationToken ct) => throw new InvalidOperationException("No network.");
    }

    private static InMemoryAppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SampleDataService Service(InMemoryAppDbContext db, Guid owner)
    {
        var user = new Owner(owner);
        var clock = new Clock(T0);
        var events = new UserEventLog(db, user, clock);
        return new SampleDataService(db, user, clock,
            new StaffingService(new StaffingRepository(db), user, clock),
            new StaffingPipelineService(db, user, clock),
            new ProgressLinkService(db, user, clock),
            new ProfileService(new ProfileRepository(db), user, clock, events),
            new Application.Campaigns.CampaignService(new CampaignRepository(db), user, clock),
            new SourceService(new SourceRepository(db), user, clock, new NoFetch()),
            new ResearchService(db, user, clock, events),
            new JobInterviewService(db, user, clock));
    }

    [Fact]
    public async Task Sales_sample_builds_a_working_pipeline_through_the_normal_rules()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var r = await Service(db, owner).CreateAsync(new("Sales"), default);

        Assert.Equal((true, 2, 4, 3, 2, 1, 2, 1, false), (r.SalesCreated, r.Accounts, r.Contacts, r.Candidates, r.Deals, r.Submissions,
            r.StaffingInterviews, r.ProgressLinks, r.CandidateCreated));
        Assert.All(await db.StaffingAccounts.ToListAsync(), a => Assert.EndsWith(SampleDataService.Suffix, a.Name));
        Assert.All(await db.StaffingContacts.ToListAsync(), c => Assert.StartsWith("+91 98", c.Phone));
        Assert.All(await db.StaffingContacts.ToListAsync(), c => Assert.EndsWith("@example.com", c.Email));

        var bench = await db.StaffingCandidates.ToListAsync();
        Assert.Contains(bench, c => c.Availability == CandidateAvailability.Immediate);
        Assert.Contains(bench, c => c.Availability == CandidateAvailability.NoticePeriod && c.NoticePeriodDays == 30);
        Assert.Contains(bench, c => c.Consent == CandidateConsent.Granted);
        Assert.All(bench, c => Assert.Equal("INR", c.RateCurrency));

        var deals = await db.StaffingDeals.ToListAsync();
        Assert.Equal(2, deals.Select(d => d.Stage).Distinct().Count());
        Assert.Contains(deals, d => d.Stage == StaffingDealStage.Interviewing);
        Assert.Equal(SubmissionState.Sent, (await db.StaffingSubmissions.SingleAsync()).State);

        var interviews = await db.StaffingInterviews.OrderBy(i => i.Round).ToListAsync();
        Assert.Equal([InterviewMode.Video, InterviewMode.Phone], interviews.Select(i => i.Mode!.Value));
        Assert.StartsWith("https://meet.google.com/", interviews[0].Location);
        Assert.All(interviews, i => Assert.True(i.ScheduledAt > T0 && i.ScheduledAt < T0.AddDays(5)));

        var link = await db.CandidateProgressLinks.SingleAsync();
        Assert.True(link.Show.HasFlag(ProgressShow.Interviews));
        Assert.All(await db.StaffingAccounts.ToListAsync(), a => Assert.Equal(owner, a.OwnerId));
    }

    [Fact]
    public async Task Running_it_again_creates_nothing_twice()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        await Service(db, owner).CreateAsync(null, default);
        var again = await Service(db, owner).CreateAsync(null, default);

        Assert.False(again.SalesCreated);
        Assert.False(again.CandidateCreated);
        Assert.Equal(2, await db.StaffingAccounts.CountAsync());
        Assert.Equal(1, await db.Campaigns.CountAsync());
        Assert.Equal(1, await db.ResearchJobs.CountAsync());
    }

    [Fact]
    public async Task Another_owner_gets_their_own_sample()
    {
        var db = NewDb();
        await Service(db, Guid.NewGuid()).CreateAsync(new("Sales"), default);
        var other = await Service(db, Guid.NewGuid()).CreateAsync(new("Sales"), default);
        Assert.True(other.SalesCreated);
        Assert.Equal(4, await db.StaffingAccounts.CountAsync());
    }

    [Fact]
    public async Task Candidate_sample_queues_research_and_adds_interviews_once_the_jobs_exist()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var first = await Service(db, owner).CreateAsync(new("candidate"), default);

        Assert.Equal((true, 1, 1, 1, 0, true), (first.CandidateCreated, first.Profiles, first.Campaigns, first.Sources, first.JobInterviews,
            first.JobInterviewsPending));
        Assert.NotNull(first.ResearchJobId);
        Assert.False(first.SalesCreated);
        var source = await db.Sources.SingleAsync();
        Assert.Equal(3, PasteParser.Parse(source.Text).Count);
        Assert.Equal(ProfileType.Candidate, (await db.Profiles.SingleAsync()).Type);

        // Research found the jobs (the processor normally does this in the background).
        var campaign = await db.Campaigns.SingleAsync();
        foreach (var (title, org, score) in new[] { ("Data Scientist", "Kestrel Analytics", 82), ("Software Engineer", "Zylo Payments", 74), ("Full Stack", "Brightleaf", 60) })
        {
            var o = new Opportunity(owner, campaign.Id, OpportunityMode.Job, Guid.NewGuid().ToString("N"), T0);
            o.ApplyResearch(title, org, "Bengaluru", null, null, null, null, null, score, score, FilterOutcome.Qualified, null, "[]", "[]", "[]", 0, first.ResearchJobId!.Value, T0);
            db.Opportunities.Add(o);
        }
        await db.SaveChangesAsync();

        var second = await Service(db, owner).CreateAsync(new("Candidate"), default);
        Assert.Equal((false, 2, false), (second.CandidateCreated, second.JobInterviews, second.JobInterviewsPending));
        var interviews = await db.JobInterviews.ToListAsync();
        Assert.Equal(2, interviews.Count);
        Assert.All(interviews, i => Assert.Equal(JobInterviewStatus.Scheduled, i.Status));
        var top = await db.Opportunities.SingleAsync(o => o.Organization == "Kestrel Analytics");
        Assert.Contains(interviews, i => i.OpportunityId == top.Id && i.MeetingUrl != null);

        var third = await Service(db, owner).CreateAsync(new("Candidate"), default);
        Assert.Equal((0, false), (third.JobInterviews, third.JobInterviewsPending));
        Assert.Equal(2, await db.JobInterviews.CountAsync());
    }

    [Fact]
    public async Task An_existing_candidate_profile_is_reused()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        db.Profiles.Add(new Profile(owner, ProfileType.Candidate, "Mine", "{}", true, T0));
        await db.SaveChangesAsync();
        var r = await Service(db, owner).CreateAsync(new("Candidate"), default);
        Assert.Equal(0, r.Profiles);
        Assert.Equal(1, await db.Profiles.CountAsync());
    }

    [Fact]
    public async Task An_unknown_workspace_is_rejected() =>
        await Assert.ThrowsAsync<RequestValidationException>(() => Service(NewDb(), Guid.NewGuid()).CreateAsync(new("Investor"), default));
}
