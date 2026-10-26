using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Interviews;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Interviews;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Staffing;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>Interview tracking: the candidate's own job interviews, the sales tracker across deals, and the candidate's progress link.</summary>
public class InterviewTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private static InMemoryAppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Opportunity Lead(Guid owner, OpportunityMode mode, string key)
    {
        var o = new Opportunity(owner, Guid.NewGuid(), mode, key, T0);
        o.ApplyResearch("Senior .NET Engineer", "Northwind", "Pune", null, null, JobPlatform.LinkedIn, "1", null, 80, 80, FilterOutcome.Qualified,
            null, "[]", "[]", "[]", 0, Guid.NewGuid(), T0);
        return o;
    }

    // ---------- Candidate: job interviews ----------

    [Fact]
    public void A_job_interview_needs_a_round_in_range_and_an_http_link()
    {
        var owner = Guid.NewGuid();
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobInterview(owner, Guid.NewGuid(), 0, null, null, null, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobInterview(owner, Guid.NewGuid(), 21, null, null, null, T0));
        Assert.Throws<ArgumentException>(() => new JobInterview(owner, Guid.NewGuid(), 1, null, "javascript:alert(1)", null, T0));
        var i = new JobInterview(owner, Guid.NewGuid(), 2, T0.AddDays(1), " https://meet.example.com/abc ", "  ", T0);
        Assert.Equal((2, "https://meet.example.com/abc", (string?)null, JobInterviewStatus.Scheduled, 1), (i.Round, i.MeetingUrl, i.Notes, i.Status, i.Version));
    }

    [Fact]
    public void An_interview_without_a_time_cannot_be_done_or_missed_but_can_be_cancelled()
    {
        var i = new JobInterview(Guid.NewGuid(), Guid.NewGuid(), 1, null, null, null, T0);
        Assert.Throws<InvalidOperationException>(() => i.SetStatus(JobInterviewStatus.Completed, T0));
        Assert.Throws<InvalidOperationException>(() => i.SetStatus(JobInterviewStatus.NoShow, T0));
        i.SetStatus(JobInterviewStatus.Cancelled, T0);
        Assert.Equal((JobInterviewStatus.Cancelled, 2), (i.Status, i.Version));
        i.SetStatus(JobInterviewStatus.Cancelled, T0);
        Assert.Equal(2, i.Version);
    }

    [Fact]
    public async Task Rounds_count_up_per_job_and_the_list_shows_the_job_soonest_first()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var job = Lead(owner, OpportunityMode.Job, "job:1");
        db.Add(job);
        await db.SaveChangesAsync();
        var svc = new JobInterviewService(db, new Owner(owner), new Clock(T0));

        var first = await svc.CreateAsync(job.Id, new SaveJobInterviewRequest(null, T0.AddDays(3), "https://meet.example.com/a", null), default);
        var second = await svc.CreateAsync(job.Id, new SaveJobInterviewRequest(null, T0.AddDays(1), null, "Bring the portfolio"), default);
        var undated = await svc.CreateAsync(job.Id, new SaveJobInterviewRequest(null, null, null, null), default);

        Assert.Equal((1, 2, 3), (first.Round, second.Round, undated.Round));
        var list = await svc.ListAsync(null, default);
        Assert.Equal(new[] { second.Id, first.Id, undated.Id }, list.Select(x => x.Id));
        Assert.All(list, x => Assert.Equal(("Senior .NET Engineer", "Northwind"), (x.Title, x.Organization)));
        Assert.Equal(3, (await svc.ListAsync(job.Id, default)).Count);
    }

    [Fact]
    public async Task Status_and_edits_need_the_loaded_version()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var job = Lead(owner, OpportunityMode.Job, "job:1");
        db.Add(job);
        await db.SaveChangesAsync();
        var svc = new JobInterviewService(db, new Owner(owner), new Clock(T0));
        var i = await svc.CreateAsync(job.Id, new SaveJobInterviewRequest(1, T0.AddDays(1), null, null), default);

        var done = await svc.SetStatusAsync(i.Id, new JobInterviewStatusRequest(JobInterviewStatus.Completed, i.Version), default);
        Assert.Equal(JobInterviewStatus.Completed, done.Status);
        await Assert.ThrowsAsync<ConflictException>(() => svc.SetStatusAsync(i.Id, new JobInterviewStatusRequest(JobInterviewStatus.NoShow, i.Version), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.UpdateAsync(i.Id, new SaveJobInterviewRequest(2, null, null, null), default));
        var edited = await svc.UpdateAsync(i.Id, new SaveJobInterviewRequest(2, T0.AddDays(2), "https://meet.example.com/x", null, done.Version), default);
        Assert.Equal((2, "https://meet.example.com/x"), (edited.Round, edited.MeetingUrl));
    }

    [Fact]
    public async Task Bad_input_company_leads_and_other_owners_are_refused()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var job = Lead(owner, OpportunityMode.Job, "job:1");
        var company = Lead(owner, OpportunityMode.Customer, "co:1");
        var foreign = Lead(Guid.NewGuid(), OpportunityMode.Job, "job:2");
        db.AddRange(job, company, foreign);
        await db.SaveChangesAsync();
        var svc = new JobInterviewService(db, new Owner(owner), new Clock(T0));

        var bad = await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateAsync(job.Id,
            new SaveJobInterviewRequest(0, DateTime.SpecifyKind(T0, DateTimeKind.Unspecified), "ftp://x", null), default));
        Assert.Contains("round", bad.Errors.Keys);
        Assert.Contains("scheduledAt", bad.Errors.Keys);
        Assert.Contains("meetingUrl", bad.Errors.Keys);
        await Assert.ThrowsAsync<RequestValidationException>(() => svc.CreateAsync(company.Id, new SaveJobInterviewRequest(null, null, null, null), default));
        await Assert.ThrowsAsync<NotFoundException>(() => svc.CreateAsync(foreign.Id, new SaveJobInterviewRequest(null, null, null, null), default));

        var mine = await svc.CreateAsync(job.Id, new SaveJobInterviewRequest(null, null, null, null), default);
        var other = new JobInterviewService(db, new Owner(Guid.NewGuid()), new Clock(T0));
        Assert.Empty(await other.ListAsync(null, default));
        await Assert.ThrowsAsync<NotFoundException>(() => other.DeleteAsync(mine.Id, default));
        await svc.DeleteAsync(mine.Id, default);
        Assert.Empty(await svc.ListAsync(null, default));
    }

    // ---------- Sales: the tracker and the candidate's progress link ----------

    private sealed record Staffing(InMemoryAppDbContext Db, Clock Clock, Guid Owner, StaffingDeal Deal, StaffingCandidate Candidate, StaffingSubmission Submission)
    {
        public StaffingPipelineService Pipeline => new(Db, new Owner(Owner), Clock);
    }

    private static async Task<Staffing> StaffingSetup()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var account = new StaffingAccount(owner, "Contoso", StaffingAccountSource.Manual, T0);
        var deal = new StaffingDeal(owner, account.Id, null, "Two .NET developers", StaffingDealSource.Manual, T0);
        var candidate = new StaffingCandidate(owner, "Asha Rao", T0);
        var submission = new StaffingSubmission(owner, deal.Id, candidate.Id, CandidateField.Name, "{}", candidate.Version, 0, null, T0);
        submission.Approve(submission.Version, T0);
        submission.MarkSent(submission.Version, HandoffChannel.Email, "msg-1", T0);
        db.AddRange(account, deal, candidate, submission);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Staffing(db, new Clock(T0), owner, deal, candidate, submission);
    }

    [Fact]
    public async Task The_tracker_lists_every_interview_with_candidate_client_and_role_upcoming_first()
    {
        var s = await StaffingSetup();
        var past = new StaffingInterview(s.Owner, s.Deal.Id, s.Submission.Id, 1, T0);
        past.Schedule(T0.AddDays(-2), "Asia/Kolkata", 45, InterviewMode.Video, null, null, null, T0);
        past.Finish(InterviewState.Completed, T0);
        var next = new StaffingInterview(s.Owner, s.Deal.Id, s.Submission.Id, 2, T0);
        next.Schedule(T0.AddDays(1), "Asia/Kolkata", 45, InterviewMode.Video, "https://meet.example.com/r2", null, "internal", T0);
        var foreign = new StaffingInterview(Guid.NewGuid(), s.Deal.Id, s.Submission.Id, 1, T0);
        s.Db.AddRange(past, next, foreign);
        await s.Db.SaveChangesAsync();

        var rows = await s.Pipeline.InterviewBoardAsync(200, default);

        Assert.Equal(new[] { next.Id, past.Id }, rows.Select(r => r.Interview.Id));
        var top = rows[0];
        Assert.Equal(("Asha Rao", "Contoso", "Two .NET developers", s.Candidate.Id), (top.CandidateName, top.Client, top.DealTitle, top.CandidateId));
        Assert.Equal(NotificationStatus.NotNotified, top.Interview.CandidateNotification);
        Assert.False(top.ProgressLinkActive);
    }

    [Fact]
    public async Task A_status_change_by_sales_shows_on_the_candidates_progress_link()
    {
        var s = await StaffingSetup();
        var interview = new StaffingInterview(s.Owner, s.Deal.Id, s.Submission.Id, 1, T0);
        interview.Schedule(T0.AddHours(2), "Asia/Kolkata", 45, InterviewMode.Video, "https://meet.example.com/r1", null, null, T0);
        s.Db.Add(interview);
        await s.Db.SaveChangesAsync();
        var link = await new ProgressLinkService(s.Db, new Owner(s.Owner), s.Clock).CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Interviews"]), default);
        var view = new ProgressViewService(s.Db, s.Clock);

        var before = (await view.ViewAsync(link.Token!, default)).Items.Single();
        Assert.Equal("Interview scheduled", before.Stage);
        Assert.Equal(1, before.Interview!.Round);
        var row = (await s.Pipeline.InterviewBoardAsync(200, default)).Single();
        Assert.True(row.ProgressLinkActive && row.ProgressShowsInterviews);

        s.Clock.Now = T0.AddHours(3);
        await s.Pipeline.FinishInterviewAsync(s.Deal.Id, interview.Id, new FinishInterviewRequest(InterviewState.Completed, row.Interview.Version), default);

        var after = (await view.ViewAsync(link.Token!, default)).Items.Single();
        Assert.Equal("Interview done, waiting for the client's answer", after.Stage);
        Assert.Null(after.Interview);

        await s.Pipeline.RequestInterviewAsync(s.Deal.Id, new RequestInterviewRequest(s.Submission.Id), default);
        Assert.Equal("The client asked for an interview", (await view.ViewAsync(link.Token!, default)).Items.Single().Stage);
    }

    [Fact]
    public async Task Note_prep_and_suggested_roles_reach_the_candidate_only_when_shared()
    {
        var s = await StaffingSetup();
        // Two rounds on one submission: the candidate's page shows the latest, so prep is offered only for that one.
        var earlier = new StaffingInterview(s.Owner, s.Deal.Id, s.Submission.Id, 1, T0);
        earlier.Schedule(T0.AddHours(5), "Asia/Kolkata", 45, InterviewMode.Video, null, null, null, T0);
        var interview = new StaffingInterview(s.Owner, s.Deal.Id, s.Submission.Id, 2, T0);
        interview.Schedule(T0.AddDays(1), "Asia/Kolkata", 45, InterviewMode.Video, "https://meet.example.com/r1", "Tech round", "internal", T0);
        var other = new StaffingDeal(s.Owner, s.Deal.AccountId, null, "Lead React developer", StaffingDealSource.Manual, T0);
        s.Db.AddRange(earlier, interview, other);
        await s.Db.SaveChangesAsync();
        var versionBefore = interview.Version;
        var links = new ProgressLinkService(s.Db, new Owner(s.Owner), s.Clock);
        var view = new ProgressViewService(s.Db, s.Clock);

        var share = await links.GetAsync(s.Candidate.Id, default);
        Assert.Equal(interview.Id, share.Interviews!.Single().Id);
        await links.SetPrepAsync(interview.Id, new SetInterviewPrepRequest("Revise system design"), default);
        Assert.Equal(versionBefore, (await s.Db.StaffingInterviews.SingleAsync(i => i.Id == interview.Id)).Version);

        // Not ticked: nothing new shows, even though a note and a role were given.
        var link = await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Interviews"], "Hidden", [other.Id]), default);
        var page = await view.ViewAsync(link.Token!, default);
        Assert.Null(page.Note);
        Assert.Null(page.Roles);
        Assert.Null(page.Items.Single().Interview!.Prep);

        // Ticked: note, prep and the role (client only with Client).
        link = await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Interviews", "Note", "Prep", "Roles", "Client"], "Client liked your profile"), default);
        page = await view.ViewAsync(link.Token!, default);
        Assert.Equal("Client liked your profile", page.Note);
        Assert.Equal("Revise system design", page.Items.Single().Interview!.Prep);
        var role = page.Roles!.Single();
        Assert.Equal(("Lead React developer", "Contoso", "None"), (role.Title, role.Client, role.Answer));

        var answered = await view.AnswerRoleAsync(link.Token!, role.Id, new RoleAnswerRequest("NotForMe"), default);
        Assert.Equal("NotForMe", answered.Answer);
        Assert.Equal("NotForMe", (await links.GetAsync(s.Candidate.Id, default)).Roles!.Single().Answer);
        await Assert.ThrowsAsync<RequestValidationException>(() => view.AnswerRoleAsync(link.Token!, role.Id, new RoleAnswerRequest("None"), default));
        await Assert.ThrowsAsync<NotFoundException>(() => view.AnswerRoleAsync("not-a-token", role.Id, new RoleAnswerRequest("Interested"), default));

        // Re-sharing the same roles keeps the answer; an empty list removes them.
        await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Roles"], null, [other.Id]), default);
        Assert.Equal("NotForMe", (await links.GetAsync(s.Candidate.Id, default)).Roles!.Single().Answer);
        await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Roles"], null, []), default);
        Assert.Empty((await links.GetAsync(s.Candidate.Id, default)).Roles!);

        // Another team's role cannot be suggested; another team cannot write prep.
        var stranger = new StaffingDeal(Guid.NewGuid(), Guid.NewGuid(), null, "Not yours", StaffingDealSource.Manual, T0);
        s.Db.Add(stranger);
        await s.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<RequestValidationException>(() => links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Roles"], null, [stranger.Id]), default));
        await Assert.ThrowsAsync<NotFoundException>(() => new ProgressLinkService(s.Db, new Owner(Guid.NewGuid()), s.Clock).SetPrepAsync(interview.Id, new SetInterviewPrepRequest("x"), default));
    }

    [Fact]
    public async Task Resume_builder_candidate_writes_for_a_shared_role_and_sales_scores_it()
    {
        var s = await StaffingSetup();
        var links = new ProgressLinkService(s.Db, new Owner(s.Owner), s.Clock);
        var view = new ProgressViewService(s.Db, s.Clock);
        var resumes = new ResumeService(s.Db, new Owner(s.Owner), view, s.Clock);

        // Not shared yet: the builder is closed.
        var plain = await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Role"]), default);
        await Assert.ThrowsAsync<NotFoundException>(() => resumes.WorkspaceAsync(plain.Token!, default));

        Assert.Equal("Build payment APIs in .NET", await resumes.SetJobDescriptionAsync(s.Deal.Id, new JobDescriptionRequest("  Build payment APIs in .NET "), default));
        var link = await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Role", "Resume"]), default);
        var ws = await resumes.WorkspaceAsync(link.Token!, default);
        Assert.Equal("Asha Rao", ws.Name);
        var job = ws.Jobs.Single();
        Assert.Equal((s.Deal.Id, "Two .NET developers", "Build payment APIs in .NET", "Draft"), (job.DealId, job.Title, job.JobDescription, job.Resume.Status));
        Assert.Null(job.Client); // client name only when the team shares it

        await Assert.ThrowsAsync<RequestValidationException>(() => resumes.SaveAsync(link.Token!, s.Deal.Id, new SaveResumeRequest("not json", 50), default));
        await Assert.ThrowsAsync<NotFoundException>(() => resumes.SaveAsync(link.Token!, Guid.NewGuid(), new SaveResumeRequest("{}", 50), default));
        var saved = await resumes.SaveAsync(link.Token!, s.Deal.Id, new SaveResumeRequest("""{"summary":"Senior .NET engineer"}""", 72), default);
        Assert.Equal((72, "Draft"), (saved.MatchScore, saved.Status));

        var row = (await resumes.ListAsync(s.Candidate.Id, default)).Single();
        await Assert.ThrowsAsync<RequestValidationException>(() => resumes.ReviewAsync(row.Id, new ReviewResumeRequest(80, "x"), default)); // not submitted

        Assert.Equal("Submitted", (await resumes.SubmitAsync(link.Token!, s.Deal.Id, default)).Status);
        var reviewed = await resumes.ReviewAsync(row.Id, new ReviewResumeRequest(84, "Add your Kafka work to the ClaimSure project."), default);
        Assert.Equal((84, "Reviewed", "Two .NET developers"), (reviewed.SalesScore, reviewed.Status, reviewed.DealTitle));
        await Assert.ThrowsAsync<RequestValidationException>(() => resumes.ReviewAsync(row.Id, new ReviewResumeRequest(101, null), default));

        // The candidate sees the score; editing again goes back to draft but keeps the review visible.
        var again = (await resumes.WorkspaceAsync(link.Token!, default)).Jobs.Single().Resume;
        Assert.Equal((84, "Add your Kafka work to the ClaimSure project."), (again.SalesScore, again.SalesFeedback));
        Assert.Equal("Draft", (await resumes.SaveAsync(link.Token!, s.Deal.Id, new SaveResumeRequest("""{"summary":"v2"}""", 80), default)).Status);

        // Another team sees nothing.
        var other = new ResumeService(s.Db, new Owner(Guid.NewGuid()), view, s.Clock);
        await Assert.ThrowsAsync<NotFoundException>(() => other.ListAsync(s.Candidate.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => other.ReviewAsync(row.Id, new ReviewResumeRequest(10, null), default));
    }

    [Fact]
    public async Task Journey_merges_logged_progress_with_the_pipeline_and_drives_the_bench()
    {
        var s = await StaffingSetup();
        var view = new ProgressViewService(s.Db, s.Clock);
        var journey = new JourneyService(s.Db, new Owner(s.Owner), view, s.Clock);
        var links = new ProgressLinkService(s.Db, new Owner(s.Owner), s.Clock);

        var j = await journey.SetProfileAsync(s.Candidate.Id, new JourneyProfileRequest(T0.AddDays(-45), "Meera Kapoor", true), default);
        Assert.Equal((46, "Meera Kapoor", true, "available"), (j.Day, j.Salesperson, j.Priority, j.Status));
        Assert.Equal("Joined", j.Timeline.Last().Kind);

        await journey.AddEntryAsync(s.Candidate.Id, new JourneyEntryRequest("Certification", "AZ-204", "Score 862", T0.AddDays(-8), null), default);
        await journey.AddEntryAsync(s.Candidate.Id, new JourneyEntryRequest("Learning", "Microservices on Azure", null, T0.AddDays(-3), 24), default);
        await journey.AddEntryAsync(s.Candidate.Id, new JourneyEntryRequest("Certification", "AZ-400 exam", null, T0.AddDays(7), null), default);
        await Assert.ThrowsAsync<RequestValidationException>(() => journey.AddEntryAsync(s.Candidate.Id, new JourneyEntryRequest("Party", "x", null, null, null), default));

        var interview = new StaffingInterview(s.Owner, s.Deal.Id, s.Submission.Id, 1, T0);
        interview.Schedule(T0.AddDays(2), "Asia/Kolkata", 45, InterviewMode.Video, null, null, null, T0);
        s.Db.Add(interview);
        await s.Db.SaveChangesAsync();

        j = await journey.GetAsync(s.Candidate.Id, default);
        Assert.Equal("interviewing", j.Status);
        Assert.Equal(["AZ-204"], j.Certifications);              // the booked exam is upcoming, not held yet
        Assert.Equal(24, j.LearningHours);
        Assert.Equal(["Interview", "Certification"], j.Upcoming.Select(u => u.Kind));
        Assert.Contains(j.Timeline, t => t.Kind == "Learning" && t.Detail == "24 h");

        var bench = await journey.BenchAsync(default);
        var row = bench.Rows.Single();
        Assert.Equal(("interviewing", true, "Interview"), (row.Status, row.Priority, row.Next!.Kind));

        // The candidate sees their own journey only when it is shared, never the priority flag, and can log progress.
        var plain = await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Role"]), default);
        await Assert.ThrowsAsync<NotFoundException>(() => journey.PublicAsync(plain.Token!, default));
        var link = await links.CreateAsync(s.Candidate.Id, new CreateProgressLinkRequest(["Role", "Journey"]), default);
        var mine = await journey.PublicAsync(link.Token!, default);
        Assert.False(mine.Priority);
        Assert.Equal("Meera Kapoor", mine.Salesperson);
        mine = await journey.PublicAddAsync(link.Token!, new JourneyEntryRequest("Project", "Claims event pipeline", "repo link", T0.AddHours(-1), null), default);
        Assert.Contains(mine.Timeline, t => t.Title == "Claims event pipeline" && t.Source == "Candidate");

        // Another team cannot read or change it.
        var other = new JourneyService(s.Db, new Owner(Guid.NewGuid()), view, s.Clock);
        await Assert.ThrowsAsync<NotFoundException>(() => other.GetAsync(s.Candidate.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => other.DeleteEntryAsync(mine.Timeline.First(t => t.EntryId != null).EntryId!.Value, default));
    }
}
