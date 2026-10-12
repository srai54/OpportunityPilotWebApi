using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;
using OpportunityPilot.Domain.Sales;

namespace OpportunityPilot.Application.Outreach;

public sealed record ProviderExecutionDto(
    Guid Id, ExecutionSubject Subject, Guid SubjectId, int SubjectVersion, ExecutionProvider Provider, ExecutionState State,
    string IdempotencyKey, string? Receipt, string? ProviderReference, string? SafeFailure, DateTime CreatedAt, DateTime? CompletedAt,
    string Instructions, bool Reused);

public sealed record StartExecutionRequest(int Version);
/// <param name="FollowUpInDays">Days until the automatic follow-up reminder after a sent message: null = 5, 0 = none.</param>
public sealed record ConfirmExecutionRequest(string Receipt, string? ProviderReference, int? FollowUpInDays = null);
public sealed record FailExecutionRequest(string Reason);

/// <summary>
/// The one contract every provider action goes through (S13): the subject must carry a valid approval of its current
/// version, the recipient must not be suppressed, the owner's daily quota must allow it, and the idempotency key (subject
/// + approved content hash) guarantees at most one execution per approved content (S5). Gmail, Freelancer.com and Upwork
/// have no connector without credentials, so today every execution is manual: the user acts on the provider and confirms
/// with a receipt. Only a confirmed execution marks a draft Sent (and the opportunity Contacted) or a bid Placed.
/// </summary>
public sealed class ProviderGateway(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const int DailyExecutionLimit = 100;
    public const int DefaultFollowUpDays = 5;

    public async Task<ProviderExecutionDto> StartDraftAsync(Guid draftId, StartExecutionRequest r, CancellationToken ct)
    {
        var draft = await db.OutreachDrafts.FirstOrDefaultAsync(d => d.Id == draftId && d.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Draft not found.");
        if (r is null || r.Version != draft.Version) throw new ConflictException($"The draft changed (now version {draft.Version}). Review it again.");
        var existing = await ExistingAsync(ExecutionSubject.OutreachDraft, draft.Id, draft.ApprovedHash, ct);
        if (existing is not null) return ToDto(existing, reused: true);
        if (!draft.HasValidApproval()) throw Invalid("draft", "Approve this exact version before sending it.");
        if (draft.Recipient is { } recipient && await SuppressedAsync(recipient, ct))
            throw Invalid("recipient", "This recipient is on your suppression list (they asked not to be contacted).");
        return await CreateAsync(ExecutionSubject.OutreachDraft, draft.Id, draft.Version, draft.ApprovedHash!, ct);
    }

    public async Task<ProviderExecutionDto> StartBidAsync(Guid bidId, StartExecutionRequest r, CancellationToken ct)
    {
        var bid = await db.SalesBids.FirstOrDefaultAsync(b => b.Id == bidId && b.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Bid not found.");
        if (r is null || r.Version != bid.Version) throw new ConflictException($"The bid changed (now version {bid.Version}). Review it again.");
        var existing = await ExistingAsync(ExecutionSubject.SalesBid, bid.Id, bid.ApprovedContentHash, ct);
        if (existing is not null) return ToDto(existing, reused: true);
        var project = await db.SalesProjects.FirstAsync(p => p.Id == bid.ProjectId && p.OwnerId == user.OwnerId, ct);
        if (!bid.HasValidApproval(project.ApprovalContext())) throw Invalid("bid", "Approve this exact bid version before placing it.");
        project.ChangeState(SalesProjectState.ManualHandoff, Now);
        return await CreateAsync(ExecutionSubject.SalesBid, bid.Id, bid.Version, bid.ApprovedContentHash!, ct);
    }

    public async Task<ProviderExecutionDto> ConfirmAsync(Guid id, ConfirmExecutionRequest r, CancellationToken ct)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Receipt)) throw Invalid("receipt", "Say how it was sent or placed (sent item, portal reference…).");
        var execution = await FindAsync(id, ct);
        try
        {
            execution.Succeed(r.Receipt, r.ProviderReference, Now);
            switch (execution.Subject)
            {
                case ExecutionSubject.OutreachDraft:
                    var draft = await db.OutreachDrafts.FirstAsync(d => d.Id == execution.SubjectId && d.OwnerId == user.OwnerId, ct);
                    if (draft.Version != execution.SubjectVersion || draft.ApprovedHash != execution.ContentHash)
                        throw new InvalidOperationException("The draft changed after this send started; it was not marked as sent.");
                    draft.MarkSent(Now);
                    var opportunity = await db.Opportunities.FirstAsync(o => o.Id == draft.OpportunityId && o.OwnerId == user.OwnerId, ct);
                    db.Activities.Add(new Activity(user.OwnerId, opportunity.Id, ActivityKinds.StatusChanged, Now,
                        $"{draft.Channel} sent by you ({execution.Provider}): {execution.Receipt}"));
                    if (opportunity.Status is OpportunityStatus.New or OpportunityStatus.Suggested or OpportunityStatus.Shortlisted)
                        if (opportunity.ChangeStatus(OpportunityStatus.Contacted, Now) is { } moved) db.Activities.Add(moved);
                    // A message nobody follows up on is a message wasted: schedule a reminder unless one is already open.
                    var days = Math.Clamp(r.FollowUpInDays ?? DefaultFollowUpDays, 0, 60);
                    if (days > 0 && !await db.NextActions.AnyAsync(x => x.OwnerId == user.OwnerId && x.OpportunityId == opportunity.Id
                            && x.State == NextActionState.Open && x.Kind == NextActionKind.FollowUp, ct))
                        db.NextActions.Add(new NextAction(user.OwnerId, opportunity.Id, NextActionKind.FollowUp,
                            "Follow up if there is no reply (scheduled when you recorded the send).", Now.AddDays(days), "UTC", Now));
                    break;
                case ExecutionSubject.SalesBid:
                    var bid = await db.SalesBids.FirstAsync(b => b.Id == execution.SubjectId && b.OwnerId == user.OwnerId, ct);
                    var project = await db.SalesProjects.FirstAsync(p => p.Id == bid.ProjectId && p.OwnerId == user.OwnerId, ct);
                    bid.MarkPlaced(project.ApprovalContext(), Now);
                    project.ChangeState(SalesProjectState.BidPlaced, Now);
                    break;
            }
        }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        catch (ArgumentException ex) { throw Invalid("receipt", ex.Message); }
        await SaveAsync(ct);
        return ToDto(execution, reused: false);
    }

    public async Task<ProviderExecutionDto> FailAsync(Guid id, FailExecutionRequest r, CancellationToken ct)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Reason)) throw Invalid("reason", "Say what went wrong.");
        var execution = await FindAsync(id, ct);
        try
        {
            execution.Fail(r.Reason, Now);
            if (execution.Subject == ExecutionSubject.SalesBid)
                (await db.SalesBids.FirstAsync(b => b.Id == execution.SubjectId && b.OwnerId == user.OwnerId, ct)).MarkFailed(Now);
        }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        catch (ArgumentException ex) { throw Invalid("reason", ex.Message); }
        await SaveAsync(ct);
        return ToDto(execution, reused: false);
    }

    public async Task<IReadOnlyList<ProviderExecutionDto>> ListAsync(Guid subjectId, CancellationToken ct) =>
        (await db.ProviderExecutions.Where(e => e.OwnerId == user.OwnerId && e.SubjectId == subjectId)
            .OrderByDescending(e => e.CreatedAt).ToListAsync(ct)).Select(e => ToDto(e, reused: false)).ToList();

    private async Task<ProviderExecutionDto> CreateAsync(ExecutionSubject subject, Guid subjectId, int version, string hash, CancellationToken ct)
    {
        var prefix = ProviderExecution.KeyFor(subject, subjectId, hash) + ":";
        var attempt = await db.ProviderExecutions.CountAsync(e => e.OwnerId == user.OwnerId && e.IdempotencyKey.StartsWith(prefix), ct) + 1;
        var dayStart = Now.Date;
        if (await db.ProviderExecutions.CountAsync(e => e.OwnerId == user.OwnerId && e.CreatedAt >= dayStart, ct) >= DailyExecutionLimit)
            throw Invalid("quota", $"You reached today's limit of {DailyExecutionLimit} sends and placements. Try again tomorrow.");
        var execution = new ProviderExecution(user.OwnerId, subject, subjectId, version, hash, ExecutionProvider.Manual, Now, attempt);
        db.ProviderExecutions.Add(execution);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Lost a race with an identical request: return the execution that won.
            db.ProviderExecutions.Remove(execution);
            var winner = await db.ProviderExecutions.AsNoTracking()
                .FirstAsync(e => e.OwnerId == user.OwnerId && e.IdempotencyKey == execution.IdempotencyKey, ct);
            return ToDto(winner, reused: true);
        }
        return ToDto(execution, reused: false);
    }

    private Task<ProviderExecution?> ExistingAsync(ExecutionSubject subject, Guid subjectId, string? hash, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hash)) return Task.FromResult<ProviderExecution?>(null);
        var prefix = ProviderExecution.KeyFor(subject, subjectId, hash) + ":";
        // A failed attempt does not block a new one for the same content; a pending or successful one does.
        return db.ProviderExecutions.FirstOrDefaultAsync(e => e.OwnerId == user.OwnerId && e.IdempotencyKey.StartsWith(prefix) && e.State != ExecutionState.Failed, ct);
    }

    private async Task<bool> SuppressedAsync(string recipient, CancellationToken ct)
    {
        var normalized = Suppression.Normalize(recipient);
        return await db.Suppressions.AnyAsync(s => s.OwnerId == user.OwnerId && s.NormalizedRecipient == normalized, ct);
    }

    private async Task<ProviderExecution> FindAsync(Guid id, CancellationToken ct) =>
        await db.ProviderExecutions.FirstOrDefaultAsync(e => e.Id == id && e.OwnerId == user.OwnerId, ct)
        ?? throw new NotFoundException("Execution not found.");

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("This changed elsewhere. Reload before trying again."); }
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private static ProviderExecutionDto ToDto(ProviderExecution e, bool reused) =>
        new(e.Id, e.Subject, e.SubjectId, e.SubjectVersion, e.Provider, e.State, e.IdempotencyKey, e.Receipt, e.ProviderReference,
            e.SafeFailure, e.CreatedAt, e.CompletedAt,
            e.State switch
            {
                ExecutionState.AwaitingManualConfirmation => e.Subject == ExecutionSubject.SalesBid
                    ? "Place exactly this bid on the provider yourself, then confirm with the provider's reference."
                    : "Send exactly this approved text yourself, then confirm with the sent item's subject or id.",
                ExecutionState.Succeeded => "Done. The receipt is stored.",
                _ => "Not done. You can start again for the same approved version."
            },
            reused);
}
