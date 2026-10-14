using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Drafts;
using OpportunityPilot.Domain.Circle;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Outreach;

namespace OpportunityPilot.Application.Outreach;

/// <param name="Body">The exact approved text. The agent sends it unchanged or not at all.</param>
/// <param name="FitsConnectionNote">The body fits LinkedIn's connection-note limit (<see cref="LinkedInNote.MaxLength"/>); a longer one can only go as a message to a 1st-degree connection.</param>
public sealed record LinkedInOutboxItem(
    Guid DraftId, Guid OpportunityId, int Version, string ProfileUrl, string Body, bool FitsConnectionNote, string Organization, string Title,
    string? PersonName);

/// <param name="Receipt">What the agent did, e.g. "Connection request with note sent 2026-10-14 10:02 UTC".</param>
/// <param name="ProviderReference">"connect" or "message".</param>
public sealed record AgentLinkedInConfirmRequest(string Receipt, string? ProviderReference = null);

/// <summary>
/// LinkedIn notes sent by the desktop agent from the person's own logged-in browser (LI1). The API never contacts LinkedIn; it only
/// hands the agent approved text and records what the agent reports. Every send goes through <see cref="ProviderGateway"/>, so the
/// approval must match the exact version and content, the recipient must not be suppressed, the daily quota applies, and the same
/// approved content is executed at most once.
/// </summary>
public sealed class LinkedInOutboxService(IAppDbContext db, ICurrentUser user, TimeProvider clock, ProviderGateway gateway)
{
    public const int MaxOutbox = 50;
    /// <summary>Server-side ceiling on agent LinkedIn sends per UTC day, whatever the agent's own config says.</summary>
    public const int MaxLinkedInSendsPerDay = 25;

    /// <summary>
    /// Approved, unsent LinkedInMessage drafts addressed to a LinkedIn profile link, whose approval still matches, whose recipient is not
    /// suppressed, and that have no send in progress or done for the current approved content. Oldest approval first.
    /// </summary>
    public async Task<IReadOnlyList<LinkedInOutboxItem>> ListAsync(CancellationToken ct)
    {
        var owner = user.OwnerId;
        var rows = await (from d in db.OutreachDrafts
                          join o in db.Opportunities on d.OpportunityId equals o.Id
                          where d.OwnerId == owner && o.OwnerId == owner && d.Channel == DraftChannel.LinkedInMessage
                                && d.State == DraftState.Approved && d.Recipient != null && d.Recipient.StartsWith(LinkedInProfileUrl.Prefix)
                          orderby d.ApprovedAt, d.Id
                          select new { Draft = d, o.Organization, o.Title })
            .AsNoTracking().ToListAsync(ct);
        rows = rows.Where(r => r.Draft.HasValidApproval() && LinkedInProfileUrl.IsNormalized(r.Draft.Recipient)).ToList();
        if (rows.Count == 0) return [];

        var suppressed = (await db.Suppressions.Where(s => s.OwnerId == owner).Select(s => s.NormalizedRecipient).ToListAsync(ct)).ToHashSet();
        var ids = rows.Select(r => r.Draft.Id).ToList();
        var busy = (await db.ProviderExecutions
                .Where(e => e.OwnerId == owner && e.Subject == ExecutionSubject.OutreachDraft && ids.Contains(e.SubjectId) && e.State != ExecutionState.Failed)
                .Select(e => new { e.SubjectId, e.ContentHash }).ToListAsync(ct))
            .Select(e => (e.SubjectId, e.ContentHash)).ToHashSet();
        var opportunityIds = rows.Select(r => r.Draft.OpportunityId).Distinct().ToList();
        var names = (await db.CirclePeople.AsNoTracking().Where(p => p.OwnerId == owner && opportunityIds.Contains(p.OpportunityId))
                .Select(p => new { p.OpportunityId, p.ProfileUrl, p.Name }).ToListAsync(ct))
            .GroupBy(p => (p.OpportunityId, p.ProfileUrl.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First().Name);

        return rows
            .Where(r => !suppressed.Contains(Suppression.Normalize(r.Draft.Recipient)) && !busy.Contains((r.Draft.Id, r.Draft.ApprovedHash!)))
            .Take(MaxOutbox)
            .Select(r => new LinkedInOutboxItem(r.Draft.Id, r.Draft.OpportunityId, r.Draft.Version, r.Draft.Recipient!, r.Draft.Body,
                r.Draft.Body.Length <= LinkedInNote.MaxLength, r.Organization, r.Title,
                names.GetValueOrDefault((r.Draft.OpportunityId, r.Draft.Recipient!.ToLowerInvariant()))))
            .ToList();
    }

    /// <summary>
    /// Starts the send through the gateway as <see cref="ExecutionProvider.LinkedInAgent"/>. A second start for the same approved content
    /// returns the existing execution with <c>reused: true</c>; the agent must then not send again.
    /// </summary>
    public async Task<ProviderExecutionDto> StartAsync(Guid draftId, StartExecutionRequest request, CancellationToken ct)
    {
        var draft = await db.OutreachDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == draftId && d.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Draft not found.");
        if (draft.Channel != DraftChannel.LinkedInMessage)
            throw Invalid("draft", "Only LinkedIn message drafts go through the LinkedIn outbox.");
        if (!LinkedInProfileUrl.IsNormalized(draft.Recipient))
            throw Invalid("recipient", "Address the draft to a LinkedIn profile link (https://www.linkedin.com/in/…) before the agent can send it.");
        var dayStart = clock.GetUtcNow().UtcDateTime.Date;
        var sentToday = await db.ProviderExecutions.CountAsync(e => e.OwnerId == user.OwnerId && e.Provider == ExecutionProvider.LinkedInAgent
                                                                    && e.CreatedAt >= dayStart && e.State != ExecutionState.Failed, ct);
        if (sentToday >= MaxLinkedInSendsPerDay && !await HasOpenExecutionAsync(draft, ct))
            throw Invalid("quota", $"The agent already sent {MaxLinkedInSendsPerDay} LinkedIn notes today. Try again tomorrow.");
        return await gateway.StartDraftAsync(draftId, request, ct, ExecutionProvider.LinkedInAgent);
    }

    public async Task<ProviderExecutionDto> ConfirmAsync(Guid draftId, AgentLinkedInConfirmRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Receipt)) throw Invalid("receipt", "Say what the agent did on LinkedIn.");
        var execution = await PendingAsync(draftId, ct);
        return await gateway.ConfirmAsync(execution.Id, new ConfirmExecutionRequest(request.Receipt, request.ProviderReference), ct);
    }

    public async Task<ProviderExecutionDto> FailAsync(Guid draftId, FailExecutionRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Reason)) throw Invalid("reason", "Say what went wrong.");
        var execution = await PendingAsync(draftId, ct);
        return await gateway.FailAsync(execution.Id, request, ct);
    }

    private Task<bool> HasOpenExecutionAsync(OutreachDraft draft, CancellationToken ct) =>
        db.ProviderExecutions.AnyAsync(e => e.OwnerId == user.OwnerId && e.Subject == ExecutionSubject.OutreachDraft && e.SubjectId == draft.Id
                                            && e.ContentHash == draft.ApprovedHash && e.State != ExecutionState.Failed, ct);

    /// <summary>The agent's send in progress for this draft. None (never started, already confirmed or failed) is a conflict.</summary>
    private async Task<ProviderExecution> PendingAsync(Guid draftId, CancellationToken ct)
    {
        if (!await db.OutreachDrafts.AnyAsync(d => d.Id == draftId && d.OwnerId == user.OwnerId, ct)) throw new NotFoundException("Draft not found.");
        return await db.ProviderExecutions.AsNoTracking()
                   .Where(e => e.OwnerId == user.OwnerId && e.Subject == ExecutionSubject.OutreachDraft && e.SubjectId == draftId
                               && e.Provider == ExecutionProvider.LinkedInAgent && e.State == ExecutionState.AwaitingManualConfirmation)
                   .OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct)
               ?? throw new ConflictException("No LinkedIn send is in progress for this draft (it was never started, or it is already confirmed or failed).");
    }

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
