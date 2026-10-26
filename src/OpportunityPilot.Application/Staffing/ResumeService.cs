using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <param name="Data">The resume document as JSON (name, title, summary, skill groups, projects); "{}" before the first save.</param>
public sealed record ResumeDto(Guid? Id, Guid DealId, string Data, int? MatchScore, string Status, int? SalesScore, string? SalesFeedback,
    DateTime? UpdatedAt, DateTime? SubmittedAt, DateTime? ReviewedAt);
public sealed record ResumeJobDto(Guid DealId, string Title, string? Client, string? JobDescription, ResumeDto Resume);
/// <summary>What the candidate's resume page needs: the roles they may write for, and their own details to start from.</summary>
public sealed record ResumeWorkspaceDto(string Name, string? Headline, string? Skills, IReadOnlyList<ResumeJobDto> Jobs);
public sealed record SaveResumeRequest(string? Data, int? MatchScore);
public sealed record ReviewResumeRequest(int Score, string? Feedback);
public sealed record JobDescriptionRequest(string? Text);
public sealed record SalesResumeDto(Guid Id, Guid DealId, string DealTitle, string Data, int? MatchScore, string Status, int? SalesScore,
    string? SalesFeedback, DateTime UpdatedAt, DateTime? SubmittedAt, DateTime? ReviewedAt);

/// <summary>
/// Resume builder (2026-10-26): through the private progress link (when the team shares "Resume"), a candidate writes a resume for a
/// role in the agency's standard format and submits it; the sales team scores it (0–100) with feedback before it goes to the client.
/// </summary>
public sealed class ResumeService(IAppDbContext db, ICurrentUser user, ProgressViewService progress, TimeProvider clock)
{
    private static ResumeDto Dto(CandidateResume? r, Guid dealId) => r is null
        ? new ResumeDto(null, dealId, "{}", null, nameof(ResumeStatus.Draft), null, null, null, null, null)
        : new ResumeDto(r.Id, r.DealId, r.Data, r.MatchScore, r.Status.ToString(), r.SalesScore, r.SalesFeedback, r.UpdatedAt, r.SubmittedAt, r.ReviewedAt);

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>The deals a candidate may write a resume for: their submissions and the roles suggested to them.</summary>
    private async Task<List<Guid>> AllowedDealsAsync(Guid ownerId, Guid candidateId, CancellationToken ct)
    {
        var submitted = await db.StaffingSubmissions.Where(s => s.OwnerId == ownerId && s.CandidateId == candidateId).Select(s => s.DealId).ToListAsync(ct);
        var suggested = await db.ProgressRoleSuggestions.Where(r => r.OwnerId == ownerId && r.CandidateId == candidateId).Select(r => r.DealId).ToListAsync(ct);
        return submitted.Concat(suggested).Distinct().ToList();
    }

    private async Task<(CandidateProgressLink Link, StaffingCandidate Candidate)> SharedAsync(string token, CancellationToken ct)
    {
        var (link, candidate) = await progress.LinkAsync(token, ct);
        if (!link.Show.HasFlag(ProgressShow.Resume)) throw new NotFoundException("Resume builder is not shared on this link.");
        return (link, candidate);
    }

    public async Task<ResumeWorkspaceDto> WorkspaceAsync(string token, CancellationToken ct)
    {
        var (link, candidate) = await SharedAsync(token, ct);
        var dealIds = await AllowedDealsAsync(link.OwnerId, candidate.Id, ct);
        var deals = await db.StaffingDeals.Where(d => d.OwnerId == link.OwnerId && dealIds.Contains(d.Id)).ToListAsync(ct);
        var accountIds = deals.Select(d => d.AccountId).Distinct().ToList();
        var accounts = link.Show.HasFlag(ProgressShow.Client)
            ? await db.StaffingAccounts.Where(a => a.OwnerId == link.OwnerId && accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct)
            : [];
        var resumes = await db.CandidateResumes.Where(r => r.OwnerId == link.OwnerId && r.CandidateId == candidate.Id).ToListAsync(ct);
        var jobs = deals.OrderBy(d => d.Title).Select(d => new ResumeJobDto(d.Id, d.Title, accounts.GetValueOrDefault(d.AccountId), d.JobDescription,
            Dto(resumes.FirstOrDefault(r => r.DealId == d.Id), d.Id))).ToList();
        return new ResumeWorkspaceDto(candidate.Name, candidate.Headline, candidate.Skills, jobs);
    }

    public async Task<ResumeDto> SaveAsync(string token, Guid dealId, SaveResumeRequest request, CancellationToken ct)
    {
        var (link, candidate) = await SharedAsync(token, ct);
        if (!(await AllowedDealsAsync(link.OwnerId, candidate.Id, ct)).Contains(dealId)) throw new NotFoundException("Role not found.");
        var data = request?.Data?.Trim();
        if (string.IsNullOrEmpty(data) || data.Length > CandidateResume.MaxDataLength) throw Invalid("data", "The resume is empty or too long.");
        try { using var doc = JsonDocument.Parse(data); if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("data", "The resume must be a JSON object."); }
        catch (JsonException) { throw Invalid("data", "The resume is not valid JSON."); }
        if (request!.MatchScore is < 0 or > 100) throw Invalid("matchScore", "The match is 0 to 100.");
        var now = clock.GetUtcNow().UtcDateTime;
        var resume = await db.CandidateResumes.FirstOrDefaultAsync(r => r.OwnerId == link.OwnerId && r.CandidateId == candidate.Id && r.DealId == dealId, ct);
        if (resume is null) { resume = new CandidateResume(link.OwnerId, candidate.Id, dealId, now); db.CandidateResumes.Add(resume); }
        resume.Save(data, request.MatchScore, now);
        await db.SaveChangesAsync(ct);
        return Dto(resume, dealId);
    }

    public async Task<ResumeDto> SubmitAsync(string token, Guid dealId, CancellationToken ct)
    {
        var (link, candidate) = await SharedAsync(token, ct);
        var resume = await db.CandidateResumes.FirstOrDefaultAsync(r => r.OwnerId == link.OwnerId && r.CandidateId == candidate.Id && r.DealId == dealId, ct)
            ?? throw new NotFoundException("Save the resume first.");
        try { resume.Submit(clock.GetUtcNow().UtcDateTime); } catch (InvalidOperationException e) { throw Invalid("data", e.Message); }
        await db.SaveChangesAsync(ct);
        return Dto(resume, dealId);
    }

    /// <summary>The role a candidate may tailor their resume to: the link's owner, the role's title and its job description.</summary>
    public async Task<(Guid OwnerId, string Title, string? JobDescription)> RoleAsync(string token, Guid dealId, CancellationToken ct)
    {
        var (link, candidate) = await SharedAsync(token, ct);
        if (!(await AllowedDealsAsync(link.OwnerId, candidate.Id, ct)).Contains(dealId)) throw new NotFoundException("Role not found.");
        var deal = await db.StaffingDeals.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealId && d.OwnerId == link.OwnerId, ct) ?? throw new NotFoundException("Role not found.");
        return (link.OwnerId, deal.Title, deal.JobDescription);
    }

    // ---------- Sales team ----------

    public async Task<IReadOnlyList<SalesResumeDto>> ListAsync(Guid candidateId, CancellationToken ct)
    {
        _ = await db.StaffingCandidates.FirstOrDefaultAsync(c => c.Id == candidateId && c.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Candidate not found.");
        var rows = await db.CandidateResumes.AsNoTracking().Where(r => r.OwnerId == user.OwnerId && r.CandidateId == candidateId).OrderByDescending(r => r.UpdatedAt).ToListAsync(ct);
        var ids = rows.Select(r => r.DealId).ToList();
        var titles = await db.StaffingDeals.Where(d => d.OwnerId == user.OwnerId && ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
        return rows.Select(r => new SalesResumeDto(r.Id, r.DealId, titles.GetValueOrDefault(r.DealId) ?? "", r.Data, r.MatchScore, r.Status.ToString(), r.SalesScore,
            r.SalesFeedback, r.UpdatedAt, r.SubmittedAt, r.ReviewedAt)).ToList();
    }

    public async Task<SalesResumeDto> ReviewAsync(Guid resumeId, ReviewResumeRequest request, CancellationToken ct)
    {
        var r = await db.CandidateResumes.FirstOrDefaultAsync(x => x.Id == resumeId && x.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Resume not found.");
        if (request is null || request.Score is < 0 or > 100) throw Invalid("score", "Score 0 to 100.");
        try { r.Review(request.Score, request.Feedback?.Trim(), clock.GetUtcNow().UtcDateTime); }
        catch (InvalidOperationException e) { throw Invalid("score", e.Message); }
        catch (ArgumentException e) { throw Invalid("feedback", e.Message); }
        await db.SaveChangesAsync(ct);
        var title = await db.StaffingDeals.Where(d => d.Id == r.DealId).Select(d => d.Title).FirstOrDefaultAsync(ct) ?? "";
        return new SalesResumeDto(r.Id, r.DealId, title, r.Data, r.MatchScore, r.Status.ToString(), r.SalesScore, r.SalesFeedback, r.UpdatedAt, r.SubmittedAt, r.ReviewedAt);
    }

    public async Task<string?> SetJobDescriptionAsync(Guid dealId, JobDescriptionRequest request, CancellationToken ct)
    {
        var deal = await db.StaffingDeals.FirstOrDefaultAsync(d => d.Id == dealId && d.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Deal not found.");
        var text = string.IsNullOrWhiteSpace(request?.Text) ? null : request!.Text!.Trim();
        if (text is { Length: > StaffingDeal.MaxJobDescriptionLength }) throw Invalid("text", $"Keep the job description under {StaffingDeal.MaxJobDescriptionLength} characters.");
        deal.SetJobDescription(text);
        await db.SaveChangesAsync(ct);
        return deal.JobDescription;
    }

    /// <summary>The deals' job descriptions for the share panel (sales side).</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> JobDescriptionsAsync(IReadOnlyCollection<Guid> dealIds, CancellationToken ct) =>
        await db.StaffingDeals.Where(d => d.OwnerId == user.OwnerId && dealIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.JobDescription, ct);
}
