using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Interviews;
using OpportunityPilot.Application.Profiles;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Sources;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Profiles;
using OpportunityPilot.Domain.Research;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.SampleData;

/// <param name="Workspace">"Sales", "Candidate", or null for both.</param>
public sealed record SampleDataRequest(string? Workspace);

/// <summary>What this call created. A part that already existed counts 0 and its flag is false.</summary>
/// <param name="JobInterviewsPending">
/// The sample job campaign's research has not produced jobs yet, so its two interviews are still to come: call again once the
/// run has finished.
/// </param>
public sealed record SampleDataResultDto(
    bool SalesCreated, int Accounts, int Contacts, int Candidates, int Deals, int Submissions, int StaffingInterviews, int ProgressLinks,
    bool CandidateCreated, int Profiles, int Campaigns, int Sources, Guid? ResearchJobId, int JobInterviews, bool JobInterviewsPending,
    string? ProgressToken = null);

/// <summary>
/// Fills an empty account with realistic, clearly marked demo records (names end in <see cref="Suffix"/>) so a new user can try every
/// screen. Every record goes through the normal services, so consent, stage and owner rules hold. Running it again creates nothing
/// twice: each part is skipped when its marked records exist. Nothing is sent to anyone and no network call is made; the job
/// campaign's research is only queued.
/// </summary>
public sealed class SampleDataService(
    IAppDbContext db, ICurrentUser user, TimeProvider clock, IStaffingService staffing, StaffingPipelineService pipeline,
    ProgressLinkService progress, IProfileService profiles, ICampaignService campaigns, ISourceService sources, ResearchService research,
    JobInterviewService jobInterviews)
{
    public const string Suffix = " (sample)";
    public const string SalesMarker = "Northwind Technologies" + Suffix;
    public const string CampaignName = "Software and data roles, Gurugram and Bengaluru" + Suffix;

    /// <summary>India has no daylight saving, so a fixed offset is exact and needs no time zone database.</summary>
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<SampleDataResultDto> CreateAsync(SampleDataRequest? request, CancellationToken ct)
    {
        var workspace = request?.Workspace?.Trim();
        var both = string.IsNullOrEmpty(workspace);
        var sales = both || string.Equals(workspace, "Sales", StringComparison.OrdinalIgnoreCase);
        var candidate = both || string.Equals(workspace, "Candidate", StringComparison.OrdinalIgnoreCase);
        if (!sales && !candidate)
            throw new Common.RequestValidationException(new Dictionary<string, string[]> { ["workspace"] = ["Use Sales, Candidate or leave it empty for both."] });

        var s = sales ? await SalesAsync(ct) : SalesPart.None;
        var c = candidate ? await CandidateAsync(ct) : CandidatePart.None;
        return new(s.Created, s.Accounts, s.Contacts, s.Candidates, s.Deals, s.Submissions, s.Interviews, s.ProgressLinks,
            c.Created, c.Profiles, c.Campaigns, c.Sources, c.ResearchJobId, c.Interviews, c.InterviewsPending, s.ProgressToken);
    }

    private sealed record SalesPart(bool Created, int Accounts, int Contacts, int Candidates, int Deals, int Submissions, int Interviews, int ProgressLinks, string? ProgressToken = null)
    {
        public static readonly SalesPart None = new(false, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record CandidatePart(bool Created, int Profiles, int Campaigns, int Sources, Guid? ResearchJobId, int Interviews, bool InterviewsPending)
    {
        public static readonly CandidatePart None = new(false, 0, 0, 0, null, 0, false);
    }

    // ---------- Sales: accounts, contacts, bench, deals, a submission, two interviews, a progress link ----------

    private async Task<SalesPart> SalesAsync(CancellationToken ct)
    {
        var owner = user.OwnerId;
        if (await db.StaffingAccounts.AnyAsync(a => a.OwnerId == owner && a.Name == SalesMarker, ct)) return SalesPart.None;

        var northwind = await staffing.CreateAccountAsync(
            new(SalesMarker, StaffingAccountSource.Manual, null, "IT services", "Gurugram, Haryana", null), ct);
        var globex = await staffing.CreateAccountAsync(
            new("Globex Fintech" + Suffix, StaffingAccountSource.Manual, null, "Financial services", "Bengaluru, Karnataka", null), ct);

        var meera = await staffing.CreateContactAsync(northwind.Id,
            new("Meera Kapoor" + Suffix, "Head of Engineering", "meera.kapoor@example.com", false, null, "Sample contact", "+91 98100 23456"), ct);
        await staffing.CreateContactAsync(northwind.Id,
            new("Arjun Mehta" + Suffix, "Talent Acquisition Lead", "arjun.mehta@example.com", false, null, "Sample contact", "+91 98110 34567"), ct);
        var kavya = await staffing.CreateContactAsync(globex.Id,
            new("Kavya Nair" + Suffix, "VP Technology", "kavya.nair@example.com", false, null, "Sample contact", "+91 98450 45678"), ct);
        await staffing.CreateContactAsync(globex.Id,
            new("Rohan Iyer" + Suffix, "Procurement Manager", "rohan.iyer@example.com", false, null, "Sample contact", "+91 98860 56789"), ct);

        // One immediately available (consent given, submitted), one on 30 days' notice (no consent yet), one on 60 days with consent.
        var priya = await pipeline.SaveCandidateAsync(null, new(
            "Priya Sharma" + Suffix, "Senior .NET developer", "priya.sharma@example.com", "+91 98111 22334", "Gurugram",
            "C#, .NET 8, ASP.NET Core, Azure, SQL Server", 6, CandidateAvailability.Immediate, null, 250_000, "INR", RateUnit.Month,
            null, false, null), ct);
        await pipeline.SaveCandidateAsync(null, new(
            "Rahul Verma" + Suffix, "Data scientist", "rahul.verma@example.com", "+91 98204 55667", "Bengaluru",
            "Python, pandas, scikit-learn, SQL, AWS SageMaker", 4, CandidateAvailability.NoticePeriod, 30, 180_000, "INR", RateUnit.Month,
            null, false, null), ct);
        var ananya = await pipeline.SaveCandidateAsync(null, new(
            "Ananya Iyer" + Suffix, "React and Node.js engineer", "ananya.iyer@example.com", "+91 99000 77889", "Bengaluru",
            "React, TypeScript, Node.js, PostgreSQL", 5, CandidateAvailability.NoticePeriod, 60, 1_800, "INR", RateUnit.Hour,
            null, false, null), ct);
        string[] shareable = ["Name", "Headline", "Skills", "Experience", "Location", "Availability", "Rate"];
        priya = await pipeline.RecordConsentAsync(priya.Id, new(CandidateConsent.Granted, shareable, "Sample: agreed on a call", priya.Version), ct);
        await pipeline.RecordConsentAsync(ananya.Id, new(CandidateConsent.Granted, shareable, "Sample: agreed by email", ananya.Version), ct);

        // Deal 1 reaches "requirement confirmed" step by step, then the submission and interviews move it on by their own rules.
        var dotnet = await staffing.CreateDealAsync(northwind.Id, new(meera.Id, "3 senior .NET developers for a payments platform" + Suffix,
            StaffingDealSource.Manual, null, 2_400_000, "INR", "Confirm interview slots", Now.AddDays(1)), ct);
        dotnet = await MoveThroughAsync(dotnet, [
            StaffingDealStage.Qualified, StaffingDealStage.Shortlisted, StaffingDealStage.OutreachApproved, StaffingDealStage.Contacted,
            StaffingDealStage.Replied, StaffingDealStage.MeetingScheduled, StaffingDealStage.RequirementConfirmed], ct);
        var data = await staffing.CreateDealAsync(globex.Id, new(kavya.Id, "Data science team extension, 2 engineers" + Suffix,
            StaffingDealSource.Manual, null, 1_500_000, "INR", "Follow up on the intro call", Now.AddDays(2)), ct);
        await MoveThroughAsync(data, [
            StaffingDealStage.Qualified, StaffingDealStage.Shortlisted, StaffingDealStage.OutreachApproved, StaffingDealStage.Contacted], ct);

        var submission = await pipeline.SaveSubmissionAsync(dotnet.Id, null,
            new(priya.Id, ["Name", "Headline", "Skills", "Experience", "Availability", "Rate"], "Strong fit for the payments team.", null), ct);
        submission = await pipeline.ApproveSubmissionAsync(dotnet.Id, submission.Id, new(submission.Version), ct);
        await pipeline.MarkSubmissionSentAsync(dotnet.Id, submission.Id,
            new(submission.Version, HandoffChannel.Email, "Sample: profile emailed to meera.kapoor@example.com"), ct);

        var video = await pipeline.RequestInterviewAsync(dotnet.Id, new(submission.Id), ct);
        await pipeline.ScheduleInterviewAsync(dotnet.Id, video.Id, new(IstToUtc(1, 10, 30), "Asia/Kolkata", 45, InterviewMode.Video,
            "https://meet.google.com/abc-defg-hij", "Technical round on C# and system design.", "Sample interview", video.Version), ct);
        var phone = await pipeline.RequestInterviewAsync(dotnet.Id, new(submission.Id), ct);
        await pipeline.ScheduleInterviewAsync(dotnet.Id, phone.Id, new(IstToUtc(3, 15, 0), "Asia/Kolkata", 30, InterviewMode.Phone,
            "Meera Kapoor calls Priya on +91 98111 22334", "Short call with the hiring manager.", "Sample interview", phone.Version), ct);

        // Priya (sample) uses every candidate feature (2026-10-26): job descriptions, interview prep, a note, a suggested role, a journey,
        // a reviewed resume, and a link that shares all of it. Its code goes back to the browser, so the candidate view opens it at once.
        foreach (var (dealId, jd) in new[] { (dotnet.Id, SampleJdDotnet), (data.Id, SampleJdData) })
            (await db.StaffingDeals.FirstAsync(d => d.Id == dealId && d.OwnerId == user.OwnerId, ct)).SetJobDescription(jd);
        foreach (var iv in await db.StaffingInterviews.Where(i => i.OwnerId == user.OwnerId && i.SubmissionId == submission.Id).ToListAsync(ct))
            iv.SetCandidatePrep(iv.Round == 1
                ? "Revise C# async/await, EF Core performance and how you would design an idempotent payments API. Have the claims pipeline story ready (STAR)."
                : "Meera Kapoor (hiring manager) calls you. Be ready to explain why payments, your notice period, and one question about the team.");
        await db.SaveChangesAsync(ct);
        await SeedJourneysAsync(ct);
        var link = await progress.CreateAsync(priya.Id, new(["Client", "Role", "Interviews", "Offer", "Note", "Prep", "Roles", "Resume", "Journey"],
            "Great news: Northwind liked your profile. Two rounds are booked; prep notes are under each interview. Also have a look at the Globex role.",
            [data.Id]), ct);
        return new(true, 2, 4, 3, 2, 1, 2, 1, link.Token);
    }

    private const string SampleJdDotnet = "Northwind Technologies is building a new payments platform. We need senior .NET developers (5+ years) with C#, .NET 8, ASP.NET Core, Entity Framework Core, Azure, Azure Service Bus, SQL Server, Docker and Kubernetes. Microservices and event-driven design; REST APIs; CI/CD with Azure DevOps. Contract, 12 months, hybrid in Gurugram.";
    private const string SampleJdData = "Globex Fintech is extending its data science team. Python, pandas, scikit-learn, SQL, AWS SageMaker, MLflow; experience with fraud or risk models. Contract, 6 months, remote (India).";

    private const string SampleResumeJson = """{"name":"Priya Sharma (sample)","title":"Senior .NET developer | Azure | 6 years","summary":"Software engineer with 6 years building .NET payment and claims platforms on Azure. Designs microservices with ASP.NET Core, Azure Service Bus and SQL Server; processed 4M+ records daily.","skills":[{"label":"Backend","items":"C#, .NET 8, ASP.NET Core, Entity Framework Core"},{"label":"Cloud","items":"Azure, Azure Service Bus, Azure Functions, Key Vault"},{"label":"Databases","items":"SQL Server, Redis"}],"projects":[{"name":"ClaimSure - Claims Data Platform","details":"Claims platform for 150,000+ users.","teamSize":"17","technology":"C#, .NET, Azure Service Bus, SQL Server, Docker","responsibilities":"Built 5 microservices in .NET\nPublished events with Azure Service Bus topics\nTuned SQL Server queries for 4M+ records a day"}]}""";

    /// <summary>Journeys for the sample bench (2026-10-26): joining dates, aligned salespeople, certifications, learning, a project, a booked exam.</summary>
    private async Task SeedJourneysAsync(CancellationToken ct)
    {
        var owner = user.OwnerId;
        var all = await db.StaffingCandidates.Where(c => c.OwnerId == owner && c.Name.EndsWith(Suffix)).ToListAsync(ct);
        var now = Now;
        void Add(StaffingCandidate c, JourneyKind kind, string title, string? detail, int daysFromNow, int? hours = null) =>
            db.CandidateJourneyEntries.Add(new CandidateJourneyEntry(owner, c.Id, kind, title, detail, now.AddDays(daysFromNow), hours, JourneyAuthor.Candidate, now));
        foreach (var c in all)
        {
            if (c.Name.StartsWith("Priya", StringComparison.Ordinal))
            {
                c.SetJourneyProfile(now.AddDays(-45), "Meera Kapoor", true);
                Add(c, JourneyKind.Certification, "AZ-900 Azure Fundamentals", "Passed in the first week", -41);
                Add(c, JourneyKind.Learning, "C# advanced patterns", "Async, generics, LINQ performance", -38, 6);
                Add(c, JourneyKind.Learning, "System design primer", "Queues, idempotency, sagas", -27, 8);
                Add(c, JourneyKind.Learning, "Kubernetes for developers", "Deployments, services, Helm basics", -12, 10);
                Add(c, JourneyKind.InterviewPrep, "Prep: Northwind payments domain", "Read their product pages; drafted 3 questions", -2);
                Add(c, JourneyKind.Note, "Resume shared with Meera for review", null, -5);
                Add(c, JourneyKind.Learning, "Microservices on Azure", "Service Bus topics, Durable Functions, Key Vault", -32, 24);
                Add(c, JourneyKind.InterviewPrep, "Mock interview with the delivery lead", "System design 4/5, communication 4/5", -24);
                Add(c, JourneyKind.Project, "Built a claims event pipeline", ".NET, Azure Service Bus, SQL Server; repo shared with sales", -16);
                Add(c, JourneyKind.Certification, "AZ-204 Azure Developer Associate", "Score 862 / 1000", -8);
                Add(c, JourneyKind.Certification, "AZ-400 exam (booked)", "Online, proctored", 6);
                // A reviewed resume for her submitted role, so the bench shows resume strength and best fit.
                var dealId = await db.StaffingSubmissions.Where(x => x.OwnerId == owner && x.CandidateId == c.Id).Select(x => x.DealId).FirstOrDefaultAsync(ct);
                if (dealId != Guid.Empty && !await db.CandidateResumes.AnyAsync(r => r.OwnerId == owner && r.CandidateId == c.Id, ct))
                {
                    var resume = new CandidateResume(owner, c.Id, dealId, now.AddDays(-5));
                    resume.Save(SampleResumeJson, 80, now.AddDays(-5));
                    resume.Submit(now.AddDays(-5));
                    resume.Review(86, "Strong match. Add your Kubernetes work if you have any.", now.AddDays(-4));
                    db.CandidateResumes.Add(resume);
                }
            }
            else if (c.Name.StartsWith("Rahul"))
            {
                c.SetJourneyProfile(now.AddDays(-111), "Arjun Singh", true);
                Add(c, JourneyKind.Certification, "DP-100 Azure Data Scientist", null, -60);
                Add(c, JourneyKind.Project, "Fraud detection model (practice)", "scikit-learn, SageMaker", -40);
                Add(c, JourneyKind.Learning, "MLOps with MLflow", null, -20, 30);
            }
            else if (c.Name.StartsWith("Ananya"))
            {
                c.SetJourneyProfile(now.AddDays(-20), "Meera Kapoor", false);
                Add(c, JourneyKind.Certification, "Meta Front-End Developer", null, -10);
                Add(c, JourneyKind.Learning, "Next.js App Router course", "Ends next week", -3, 18);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<StaffingDealDto> MoveThroughAsync(StaffingDealDto deal, IReadOnlyList<StaffingDealStage> stages, CancellationToken ct)
    {
        foreach (var stage in stages) deal = await staffing.MoveDealAsync(deal.Id, new(stage, deal.Version), ct);
        return deal;
    }

    /// <summary>A time <paramref name="days"/> days from today, India time, as UTC.</summary>
    private DateTime IstToUtc(int days, int hour, int minute)
    {
        var localDay = (Now + Ist).Date.AddDays(days);
        return DateTime.SpecifyKind(localDay.AddHours(hour).AddMinutes(minute) - Ist, DateTimeKind.Utc);
    }

    // ---------- Candidate: profile, a job campaign over three pasted posts, queued research, two interviews ----------

    public const string SamplePosts = """
        Software Engineer, Backend
        Company: Zylo Payments
        Location: Gurugram
        URL: https://jobs.example.com/zylo-backend
        Build payment APIs in Python and Go on AWS. 2-5 years of experience with REST, PostgreSQL and Docker. Hybrid, 3 days in office.
        ---
        Data Scientist
        Company: Kestrel Analytics
        Location: Bengaluru
        URL: https://jobs.example.com/kestrel-ds
        Build forecasting and churn models in Python (pandas, scikit-learn) and SQL; present findings to product teams. 3+ years.
        ---
        Full Stack Software Engineer
        Company: Brightleaf Health
        Location: Bengaluru
        URL: https://jobs.example.com/brightleaf-fullstack
        React and TypeScript front end, Python (FastAPI) services, PostgreSQL. 3-6 years. Remote-friendly with quarterly meetups.
        """;

    private async Task<CandidatePart> CandidateAsync(CancellationToken ct)
    {
        var owner = user.OwnerId;
        var existing = await db.Campaigns.AsNoTracking().Where(c => c.OwnerId == owner && c.Name == CampaignName).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (existing is { } campaignId)
        {
            // Created before; the interviews could only be added once research had found the jobs.
            var (added, pending) = await InterviewsAsync(campaignId, ct);
            return new(false, 0, 0, 0, null, added, pending);
        }

        var created = 0;
        var profileId = await db.Profiles.AsNoTracking().Where(p => p.OwnerId == owner && p.Type == ProfileType.Candidate)
            .OrderByDescending(p => p.UpdatedAt).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (profileId is null)
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                fields = new
                {
                    offer = "Software engineer who builds reliable backend services and data pipelines.",
                    experience = "4 years: Python and Go services on AWS, PostgreSQL, Docker; 1 year of analytics with pandas.",
                    skills = "Python, Go, SQL, PostgreSQL, Docker, AWS, pandas, scikit-learn, React",
                    availability = "30 days' notice; open to Gurugram, Bengaluru or remote.",
                },
                confirmations = new { experience = true, skills = true },
            });
            profileId = (await profiles.CreateAsync(new(ProfileType.Candidate, "My candidate profile" + Suffix, data, true), ct)).Id;
            created = 1;
        }

        var criteria = new CampaignCriteria
        {
            Keywords = ["Software Engineer", "Data Scientist"],
            RequiredSkills = ["Python"],
            PreferredSkills = ["SQL", "PostgreSQL", "Docker", "AWS", "pandas"],
            CandidateYears = 4,
            Locations = ["Gurugram", "Bengaluru", "Remote"],
        };
        var campaign = await campaigns.CreateAsync(new(profileId.Value, OpportunityMode.Job, CampaignName,
            "Backend and data roles that use Python, in Gurugram or Bengaluru.", criteria, null, 25), ct);
        await sources.CreateAsync(campaign.Id, new(SourceKind.Paste, "Sample job posts", null, SamplePosts, null), ct);
        var job = await research.QueueAsync(campaign.Id, ct);
        var (interviews, stillPending) = await InterviewsAsync(campaign.Id, ct);
        return new(true, created, 1, 1, job.JobId, interviews, stillPending);
    }

    /// <summary>Two interviews on the campaign's best jobs, once research has found them and none were added yet.</summary>
    private async Task<(int Added, bool Pending)> InterviewsAsync(Guid campaignId, CancellationToken ct)
    {
        var owner = user.OwnerId;
        var jobs = await db.Opportunities.AsNoTracking()
            .Where(o => o.OwnerId == owner && o.CampaignId == campaignId && o.Mode == OpportunityMode.Job)
            .OrderByDescending(o => o.Score).ThenBy(o => o.Id).Select(o => o.Id).Take(2).ToListAsync(ct);
        if (jobs.Count == 0) return (0, true);
        if (await db.JobInterviews.AnyAsync(i => i.OwnerId == owner && jobs.Contains(i.OpportunityId), ct)) return (0, false);

        await jobInterviews.CreateAsync(jobs[0], new(1, IstToUtc(2, 11, 0), "https://meet.google.com/xyz-abcd-efg", "Sample: intro with the hiring manager."), ct);
        if (jobs.Count > 1)
            await jobInterviews.CreateAsync(jobs[1], new(1, IstToUtc(4, 16, 30), null, "Sample: phone screen with the recruiter."), ct);
        return (jobs.Count, false);
    }
}
