using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Interviews;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Interviews;

/// <summary>
/// Candidate workspace: the interviews the person records for jobs they applied to (round, time, link, status). Typed in by the
/// person; nothing is read from a calendar or sent anywhere. Owner-scoped: another owner's job or interview answers 404.
/// </summary>
public sealed class JobInterviewService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const int MaxPerOpportunity = 20;
    public const int MaxListed = 500;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The person's interviews (optionally of one job), soonest first; ones without a time last.</summary>
    public async Task<IReadOnlyList<JobInterviewDto>> ListAsync(Guid? opportunityId, CancellationToken ct)
    {
        if (opportunityId is { } oid) _ = await FindJobAsync(oid, ct);
        var query = db.JobInterviews.AsNoTracking().Where(i => i.OwnerId == user.OwnerId);
        if (opportunityId is { } id) query = query.Where(i => i.OpportunityId == id);
        var rows = await query.OrderByDescending(i => i.UpdatedAt).Take(MaxListed).ToListAsync(ct);
        var ids = rows.Select(r => r.OpportunityId).Distinct().ToList();
        var jobs = await db.Opportunities.AsNoTracking().Where(o => o.OwnerId == user.OwnerId && ids.Contains(o.Id))
            .Select(o => new { o.Id, o.Title, o.Organization }).ToDictionaryAsync(o => o.Id, ct);
        return rows
            .OrderBy(r => r.ScheduledAt is null).ThenBy(r => r.ScheduledAt).ThenBy(r => r.Round).ThenBy(r => r.Id)
            .Select(r => jobs.TryGetValue(r.OpportunityId, out var j) ? ToDto(r, j.Title, j.Organization) : ToDto(r, "Removed job", ""))
            .ToList();
    }

    public async Task<JobInterviewDto> CreateAsync(Guid opportunityId, SaveJobInterviewRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        Validate(request);
        var job = await FindJobAsync(opportunityId, ct);
        var count = await db.JobInterviews.CountAsync(i => i.OwnerId == user.OwnerId && i.OpportunityId == job.Id, ct);
        if (count >= MaxPerOpportunity) throw Invalid("round", $"A job holds at most {MaxPerOpportunity} interviews.");
        var interview = Build(() => new JobInterview(user.OwnerId, job.Id, request.Round ?? count + 1, Utc(request.ScheduledAt), request.MeetingUrl, request.Notes, Now));
        db.JobInterviews.Add(interview);
        await SaveAsync(ct);
        return ToDto(interview, job.Title, job.Organization);
    }

    public async Task<JobInterviewDto> UpdateAsync(Guid id, SaveJobInterviewRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        Validate(request);
        var interview = await FindAsync(id, request.ExpectedVersion, ct);
        Build(() => { interview.Update(request.Round ?? interview.Round, Utc(request.ScheduledAt), request.MeetingUrl, request.Notes, Now); return interview; });
        await SaveAsync(ct);
        return await ToDtoAsync(interview, ct);
    }

    /// <summary>Done, no-show, cancelled, or back to scheduled (to undo a wrong tap).</summary>
    public async Task<JobInterviewDto> SetStatusAsync(Guid id, JobInterviewStatusRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (!Enum.IsDefined(request.Status)) throw Invalid("status", "Status must be Scheduled, Completed, NoShow or Cancelled.");
        var interview = await FindAsync(id, request.ExpectedVersion, ct);
        try { interview.SetStatus(request.Status, Now); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        await SaveAsync(ct);
        return await ToDtoAsync(interview, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var interview = await db.JobInterviews.FirstOrDefaultAsync(i => i.Id == id && i.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Interview not found.");
        db.JobInterviews.Remove(interview);
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(SaveJobInterviewRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Round is < 1 or > JobInterview.MaxRound) errors["round"] = [$"Round must be 1–{JobInterview.MaxRound}."];
        if (r.ScheduledAt is { Kind: DateTimeKind.Unspecified }) errors["scheduledAt"] = ["Send the time in UTC (e.g. 2026-10-20T09:30:00Z)."];
        if (!JobInterview.IsValidLink(r.MeetingUrl)) errors["meetingUrl"] = ["Use a link that starts with https://."];
        if (r.Notes is { } n && n.Trim().Length > JobInterview.MaxNotesLength) errors["notes"] = [$"Notes must be at most {JobInterview.MaxNotesLength} characters."];
        if (errors.Count > 0) throw new RequestValidationException(errors);
    }

    private static DateTime? Utc(DateTime? at) => at?.ToUniversalTime();

    private static JobInterview Build(Func<JobInterview> make)
    {
        try { return make(); }
        catch (ArgumentException ex) { throw Invalid("interview", ex.Message); }
    }

    private async Task<Opportunity> FindJobAsync(Guid id, CancellationToken ct)
    {
        var o = await db.Opportunities.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Opportunity not found.");
        if (o.Mode != OpportunityMode.Job) throw Invalid("opportunity", "Interviews belong to job leads. For a company lead, use a staffing deal.");
        return o;
    }

    private async Task<JobInterview> FindAsync(Guid id, int? expectedVersion, CancellationToken ct)
    {
        var interview = await db.JobInterviews.FirstOrDefaultAsync(i => i.Id == id && i.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Interview not found.");
        if (expectedVersion is null) throw Invalid("expectedVersion", "Send the version you loaded (expectedVersion).");
        if (expectedVersion != interview.Version)
            throw new ConflictException($"This interview changed elsewhere (now version {interview.Version}). Reload and try again.");
        return interview;
    }

    private async Task<JobInterviewDto> ToDtoAsync(JobInterview i, CancellationToken ct)
    {
        var job = await db.Opportunities.AsNoTracking().Where(o => o.Id == i.OpportunityId && o.OwnerId == user.OwnerId)
            .Select(o => new { o.Title, o.Organization }).FirstOrDefaultAsync(ct);
        return ToDto(i, job?.Title ?? "Removed job", job?.Organization ?? "");
    }

    private static JobInterviewDto ToDto(JobInterview i, string title, string organization) =>
        new(i.Id, i.OpportunityId, title, organization, i.Round, i.ScheduledAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            i.MeetingUrl, i.Notes, i.Status, i.Version, i.CreatedAt, i.UpdatedAt);

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("This interview changed elsewhere. Reload and try again."); }
    }

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
