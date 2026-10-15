using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Staffing;

namespace OpportunityPilot.Application.Staffing;

/// <summary>
/// Candidates, submissions, interviews, feedback, offers, rate cards, proposals and KPIs for the staffing CRM. Everything
/// is owner-scoped. Nothing is sent by the server: every handoff to a client or candidate is recorded by the user with a
/// receipt, and the deal advances one stage only when such a stored fact proves the step happened.
/// </summary>
public sealed partial class StaffingPipelineService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    // ---------- Candidates (X8) ----------

    public async Task<IReadOnlyList<StaffingCandidateDto>> ListCandidatesAsync(CancellationToken ct) =>
        (await Owned(db.StaffingCandidates).OrderBy(x => x.Name).ThenBy(x => x.Id).Take(500).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<StaffingCandidateDto> GetCandidateAsync(Guid id, CancellationToken ct) => ToDto(await FindCandidateAsync(id, ct));

    public async Task<StaffingCandidateDto> SaveCandidateAsync(Guid? id, SaveStaffingCandidateRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        StaffingCandidate candidate;
        if (id is { } existing)
        {
            candidate = await FindCandidateAsync(existing, ct);
            if (r.ExpectedVersion != candidate.Version) throw Stale("candidate", candidate.Version);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(r.Name)) throw Invalid("name", "Candidate name is required.");
            candidate = new StaffingCandidate(user.OwnerId, r.Name, Now);
            db.StaffingCandidates.Add(candidate);
        }
        try
        {
            candidate.Update(r.Name, r.Headline, r.Email, r.Phone, r.Location, r.Skills, r.YearsExperience, r.Availability,
                r.NoticePeriodDays, r.RateAmount, r.RateCurrency, r.RateUnit,
                // Omitted (null) keeps the stored resume; an empty string clears it.
                r.ResumeText is null ? candidate.ResumeText : r.ResumeText, r.NotifyByEmail, Now);
        }
        catch (ArgumentException ex) { throw Invalid("candidate", ex.Message); }
        await SaveAsync(ct);
        return ToDto(candidate);
    }

    public async Task<StaffingCandidateDto> RecordConsentAsync(Guid id, RecordConsentRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        var candidate = await FindCandidateAsync(id, ct);
        if (r.ExpectedVersion != candidate.Version) throw Stale("candidate", candidate.Version);
        var fields = ParseFields(r.ShareableFields, "shareableFields");
        try { candidate.RecordConsent(r.Consent, fields, r.Evidence, Now); }
        catch (ArgumentException ex) { throw Invalid("consent", ex.Message); }
        await SaveAsync(ct);
        return ToDto(candidate);
    }

    // ---------- Deal work: submissions (X9), interviews (X10), feedback (X11), offers (X12), proposals (X4) ----------

    public async Task<StaffingDealWorkDto> DealWorkAsync(Guid dealId, CancellationToken ct)
    {
        _ = await FindDealAsync(dealId, ct);
        var submissions = await Owned(db.StaffingSubmissions).Where(x => x.DealId == dealId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var candidateIds = submissions.Select(x => x.CandidateId).Distinct().ToArray();
        var candidates = await Owned(db.StaffingCandidates).Where(x => candidateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var interviews = await Owned(db.StaffingInterviews).Where(x => x.DealId == dealId).OrderBy(x => x.Round).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var feedback = await Owned(db.StaffingFeedbackEntries).Where(x => x.DealId == dealId).OrderByDescending(x => x.RecordedAt).ToListAsync(ct);
        var offers = await Owned(db.StaffingOffers).Where(x => x.DealId == dealId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var proposals = await Owned(db.StaffingProposals).Where(x => x.DealId == dealId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return new(submissions.Select(s => ToDto(s, candidates.GetValueOrDefault(s.CandidateId))).ToList(),
            interviews.Select(ToDto).ToList(), feedback.Select(ToDto).ToList(), offers.Select(ToDto).ToList(), proposals.Select(ToDto).ToList());
    }

    /// <summary>
    /// Every interview across deals and candidates for the sales tracker: upcoming first (soonest first), then the rest, newest change
    /// first. At most <paramref name="take"/> rows.
    /// </summary>
    public async Task<IReadOnlyList<StaffingInterviewBoardRowDto>> InterviewBoardAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 500);
        var interviews = await Owned(db.StaffingInterviews).AsNoTracking().OrderByDescending(x => x.UpdatedAt).Take(take).ToListAsync(ct);
        var submissionIds = interviews.Select(x => x.SubmissionId).Distinct().ToList();
        var dealIds = interviews.Select(x => x.DealId).Distinct().ToList();
        var submissions = await Owned(db.StaffingSubmissions).AsNoTracking().Where(x => submissionIds.Contains(x.Id))
            .Select(x => new { x.Id, x.CandidateId }).ToDictionaryAsync(x => x.Id, ct);
        var candidateIds = submissions.Values.Select(x => x.CandidateId).Distinct().ToList();
        var candidates = await Owned(db.StaffingCandidates).AsNoTracking().Where(x => candidateIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.Email }).ToDictionaryAsync(x => x.Id, ct);
        var deals = await Owned(db.StaffingDeals).AsNoTracking().Where(x => dealIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Title, x.AccountId }).ToDictionaryAsync(x => x.Id, ct);
        var accountIds = deals.Values.Select(x => x.AccountId).Distinct().ToList();
        var accounts = await Owned(db.StaffingAccounts).AsNoTracking().Where(x => accountIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var links = (await db.CandidateProgressLinks.AsNoTracking()
                .Where(l => l.OwnerId == user.OwnerId && l.RevokedAt == null && candidateIds.Contains(l.CandidateId))
                .Select(l => new { l.CandidateId, l.Show, l.CreatedAt }).ToListAsync(ct))
            .GroupBy(l => l.CandidateId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.CreatedAt).First().Show);

        var now = Now;
        return interviews
            .OrderBy(i => i.State is InterviewState.Requested or InterviewState.Scheduled && (i.ScheduledAt is null || i.ScheduledAt >= now.AddHours(-1)) ? 0 : 1)
            .ThenBy(i => i.State is InterviewState.Requested or InterviewState.Scheduled ? i.ScheduledAt ?? DateTime.MaxValue : DateTime.MaxValue)
            .ThenByDescending(i => i.UpdatedAt).ThenBy(i => i.Id)
            .Select(i =>
            {
                var candidateId = submissions.TryGetValue(i.SubmissionId, out var s) ? s.CandidateId : Guid.Empty;
                var c = candidates.GetValueOrDefault(candidateId);
                var d = deals.GetValueOrDefault(i.DealId);
                var hasLink = links.TryGetValue(candidateId, out var show);
                return new StaffingInterviewBoardRowDto(ToDto(i), candidateId, c?.Name ?? "Removed candidate", c?.Email, d?.Title ?? "Removed deal",
                    d is not null ? accounts.GetValueOrDefault(d.AccountId) : null, hasLink, hasLink && show.HasFlag(ProgressShow.Interviews));
            })
            .ToList();
    }

    public async Task<StaffingSubmissionDto> SaveSubmissionAsync(Guid dealId, Guid? id, SaveSubmissionRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        var deal = await FindDealAsync(dealId, ct);
        var candidate = await FindCandidateAsync(r.CandidateId, ct);
        if (candidate.Consent != CandidateConsent.Granted)
            throw Invalid("candidateId", "This candidate has not given consent to be shared. Record their consent first.");
        var fields = ParseFields(r.SharedFields, "sharedFields");
        var notAllowed = fields & ~candidate.ShareableFields;
        if (notAllowed != CandidateField.None)
            throw Invalid("sharedFields", $"The candidate has not allowed sharing: {string.Join(", ", Names(notAllowed))}.");
        var snapshot = JsonSerializer.Serialize(Snapshot(candidate, fields), Json);

        StaffingSubmission submission;
        try
        {
            if (id is { } existing)
            {
                submission = await FindAsync(db.StaffingSubmissions, existing, "Submission", ct);
                if (submission.DealId != dealId) throw new NotFoundException("Submission not found.");
                if (r.ExpectedVersion != submission.Version) throw Stale("submission", submission.Version);
                if (submission.CandidateId != candidate.Id) throw Invalid("candidateId", "A submission's candidate cannot change; create a new one.");
                submission.Revise(fields, snapshot, candidate.Version, candidate.ResumeVersion, r.Note, Now);
            }
            else
            {
                if (await Owned(db.StaffingSubmissions).AnyAsync(x => x.DealId == dealId && x.CandidateId == candidate.Id && x.State != SubmissionState.Withdrawn, ct))
                    throw new ConflictException("This candidate already has a submission on this deal.");
                submission = new StaffingSubmission(user.OwnerId, dealId, candidate.Id, fields, snapshot, candidate.Version, candidate.ResumeVersion, r.Note, Now);
                db.StaffingSubmissions.Add(submission);
            }
        }
        catch (ArgumentException ex) { throw Invalid("submission", ex.Message); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        Log(deal, StaffingDealActivityType.SubmissionChanged, $"Submission for {candidate.Name}: draft saved ({Names(fields).Count} fields).");
        await SaveAsync(ct);
        return ToDto(submission, candidate);
    }

    public Task<StaffingSubmissionDto> ApproveSubmissionAsync(Guid dealId, Guid id, VersionRequest r, CancellationToken ct) =>
        SubmissionActionAsync(dealId, id, async (deal, s, c) =>
        {
            // Consent can be withdrawn after the draft: re-check, and refuse if the candidate's shared facts changed since.
            if (c.Consent != CandidateConsent.Granted) throw new ConflictException("The candidate's consent is no longer granted.");
            if ((s.SharedFields & ~c.ShareableFields) != CandidateField.None) throw new ConflictException("The candidate no longer allows sharing some of these fields.");
            if (c.Version != s.CandidateVersion) throw new ConflictException("The candidate's details changed since this draft. Save the submission again to refresh it.");
            s.Approve(r.ExpectedVersion, Now);
            Log(deal, StaffingDealActivityType.SubmissionChanged, $"Submission for {c.Name} approved (version {s.Version}).");
            await Task.CompletedTask;
        }, ct);

    public Task<StaffingSubmissionDto> MarkSubmissionSentAsync(Guid dealId, Guid id, MarkHandoffRequest r, CancellationToken ct) =>
        SubmissionActionAsync(dealId, id, async (deal, s, c) =>
        {
            if (r is null) throw Invalid("body", "Request body is required.");
            try { s.MarkSent(r.ExpectedVersion, r.Channel, r.Receipt, Now); }
            catch (ArgumentException ex) { throw Invalid("receipt", ex.Message); }
            Log(deal, StaffingDealActivityType.ManualActionConfirmed, $"Submission for {c.Name} handed over by {r.Channel}: {s.Receipt}");
            Advance(deal, StaffingDealStage.CandidatesSubmitted);
            await Task.CompletedTask;
        }, ct);

    public Task<StaffingSubmissionDto> WithdrawSubmissionAsync(Guid dealId, Guid id, CancellationToken ct) =>
        SubmissionActionAsync(dealId, id, async (deal, s, c) =>
        {
            s.Withdraw(Now);
            Log(deal, StaffingDealActivityType.SubmissionChanged, $"Submission for {c.Name} withdrawn.");
            await Task.CompletedTask;
        }, ct);

    public async Task<StaffingInterviewDto> RequestInterviewAsync(Guid dealId, RequestInterviewRequest r, CancellationToken ct)
    {
        var deal = await FindDealAsync(dealId, ct);
        var submission = await FindAsync(db.StaffingSubmissions, r?.SubmissionId ?? Guid.Empty, "Submission", ct);
        if (submission.DealId != dealId) throw new NotFoundException("Submission not found.");
        if (submission.State != SubmissionState.Sent) throw new ConflictException("Interviews follow a submission that was sent to the client.");
        var round = await Owned(db.StaffingInterviews).CountAsync(x => x.SubmissionId == submission.Id, ct) + 1;
        var interview = new StaffingInterview(user.OwnerId, dealId, submission.Id, round, Now);
        db.StaffingInterviews.Add(interview);
        Log(deal, StaffingDealActivityType.InterviewChanged, $"Interview round {round} requested.");
        await SaveAsync(ct);
        return ToDto(interview);
    }

    public Task<StaffingInterviewDto> ScheduleInterviewAsync(Guid dealId, Guid id, ScheduleInterviewRequest r, CancellationToken ct) =>
        InterviewActionAsync(dealId, id, r?.ExpectedVersion, (deal, i) =>
        {
            if (r!.ScheduledAt.Kind == DateTimeKind.Unspecified) throw Invalid("scheduledAt", "Send the time in UTC (e.g. 2026-10-12T09:30:00Z).");
            if (!ValidTimeZone(r.TimeZone)) throw Invalid("timeZone", "Use an IANA time zone, e.g. Asia/Kolkata.");
            var rescheduled = i.State == InterviewState.Scheduled;
            i.Schedule(r.ScheduledAt.ToUniversalTime(), r.TimeZone, r.DurationMinutes, r.Mode, r.Location, r.CandidateNotes, r.InternalNotes, Now);
            Log(deal, StaffingDealActivityType.InterviewChanged,
                $"Interview round {i.Round} {(rescheduled ? "rescheduled" : "scheduled")} for {i.ScheduledAt:yyyy-MM-dd HH:mm} UTC ({i.TimeZone}).");
            Advance(deal, StaffingDealStage.Interviewing);
        }, ct);

    public Task<StaffingInterviewDto> FinishInterviewAsync(Guid dealId, Guid id, FinishInterviewRequest r, CancellationToken ct) =>
        InterviewActionAsync(dealId, id, r?.ExpectedVersion, (deal, i) =>
        {
            i.Finish(r!.Outcome, Now);
            Log(deal, StaffingDealActivityType.InterviewChanged, $"Interview round {i.Round}: {r.Outcome}.");
        }, ct);

    public Task<StaffingInterviewDto> RecordCandidateNotifiedAsync(Guid dealId, Guid id, NotifyCandidateRequest r, CancellationToken ct) =>
        InterviewActionAsync(dealId, id, r?.ExpectedVersion, (deal, i) =>
        {
            i.RecordCandidateNotified(r!.Status, Now);
            Log(deal, StaffingDealActivityType.ManualActionConfirmed, $"Candidate told about interview round {i.Round} ({r.Status}).");
        }, ct);

    public async Task<StaffingFeedbackDto> RecordFeedbackAsync(Guid dealId, RecordFeedbackRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        var deal = await FindDealAsync(dealId, ct);
        var submission = await FindAsync(db.StaffingSubmissions, r.SubmissionId, "Submission", ct);
        if (submission.DealId != dealId) throw new NotFoundException("Submission not found.");
        if (r.InterviewId is { } interviewId &&
            !await Owned(db.StaffingInterviews).AnyAsync(x => x.Id == interviewId && x.SubmissionId == submission.Id, ct))
            throw new NotFoundException("Interview not found for this submission.");
        StaffingFeedback feedback;
        try { feedback = new StaffingFeedback(user.OwnerId, dealId, submission.Id, r.InterviewId, r.Source, r.Decision, r.Detail, r.SharedWithCandidate, Now); }
        catch (ArgumentException ex) { throw Invalid("feedback", ex.Message); }
        db.StaffingFeedbackEntries.Add(feedback);
        Log(deal, StaffingDealActivityType.FeedbackRecorded,
            $"{r.Source} feedback recorded{(r.Decision == FeedbackDecision.None ? "" : $": {r.Decision}")}{(r.SharedWithCandidate ? " (shared with the candidate)" : "")}.");
        await SaveAsync(ct);
        return ToDto(feedback);
    }

    public async Task<StaffingOfferDto> SaveOfferAsync(Guid dealId, Guid? id, SaveOfferRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        var deal = await FindDealAsync(dealId, ct);
        StaffingOffer offer;
        if (id is { } existing)
        {
            offer = await FindAsync(db.StaffingOffers, existing, "Offer", ct);
            if (offer.DealId != dealId) throw new NotFoundException("Offer not found.");
            if (r.ExpectedVersion != offer.Version) throw Stale("offer", offer.Version);
        }
        else
        {
            var submission = await FindAsync(db.StaffingSubmissions, r.SubmissionId, "Submission", ct);
            if (submission.DealId != dealId) throw new NotFoundException("Submission not found.");
            if (submission.State != SubmissionState.Sent) throw new ConflictException("An offer follows a submission that was sent to the client.");
            offer = new StaffingOffer(user.OwnerId, dealId, submission.Id, Now);
            db.StaffingOffers.Add(offer);
        }
        try { offer.SetTerms(r.ClientRate, r.CandidatePay, r.Currency, r.Unit, r.StartDate, r.PlacementValue, r.Notes, Now); }
        catch (ArgumentException ex) { throw Invalid("offer", ex.Message); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        Log(deal, StaffingDealActivityType.OfferChanged, "Offer terms saved.");
        await SaveAsync(ct);
        return ToDto(offer);
    }

    public Task<StaffingOfferDto> MoveOfferAsync(Guid dealId, Guid id, MoveOfferRequest r, CancellationToken ct) =>
        OfferActionAsync(dealId, id, r?.ExpectedVersion, (deal, o) =>
        {
            o.MoveTo(r!.State, Now);
            Log(deal, StaffingDealActivityType.OfferChanged, $"Offer {r.State.ToString().ToLowerInvariant()}.");
            if (r.State == OfferState.Extended) Advance(deal, StaffingDealStage.Offer);
        }, ct);

    public Task<StaffingOfferDto> UpdateContractAsync(Guid dealId, Guid id, UpdateContractRequest r, CancellationToken ct) =>
        OfferActionAsync(dealId, id, r?.ExpectedVersion, (deal, o) =>
        {
            o.UpdateContract(r!.Status, r.ContractVersion, r.SignatureProvider, r.SignedDocumentReference, Now);
            Log(deal, StaffingDealActivityType.OfferChanged, $"Contract {r.Status}{(r.ContractVersion is null ? "" : $" (version {r.ContractVersion})")}.");
            if (r.Status is ContractStatus.Sent or ContractStatus.Signed) Advance(deal, StaffingDealStage.Contracting);
        }, ct);

    public Task<StaffingOfferDto> RecordOutcomeAsync(Guid dealId, Guid id, RecordOutcomeRequest r, CancellationToken ct) =>
        OfferActionAsync(dealId, id, r?.ExpectedVersion, (deal, o) =>
        {
            o.RecordOutcome(r!.Outcome, Now);
            Log(deal, StaffingDealActivityType.OfferChanged, $"Placement outcome: {r.Outcome}.");
            if (r.Outcome == PlacementOutcome.Placed) Advance(deal, StaffingDealStage.Won);
        }, ct);

    // ---------- Rate cards and proposals (X4) ----------

    public async Task<IReadOnlyList<StaffingRateCardDto>> ListRateCardsAsync(CancellationToken ct) =>
        (await Owned(db.StaffingRateCards).OrderBy(x => x.Status).ThenBy(x => x.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<StaffingRateCardDto> SaveRateCardAsync(Guid? id, SaveRateCardRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        var lines = ValidLines(r.Lines);
        StaffingRateCard card;
        try
        {
            if (id is { } existing)
            {
                card = await FindAsync(db.StaffingRateCards, existing, "Rate card", ct);
                if (r.ExpectedVersion != card.Version) throw Stale("rate card", card.Version);
                card.Set(r.Name, r.Currency, JsonSerializer.Serialize(lines, Json), r.Terms, r.ValidUntil, Now);
            }
            else
            {
                card = new StaffingRateCard(user.OwnerId, r.Name, r.Currency, JsonSerializer.Serialize(lines, Json), r.Terms, r.ValidUntil, Now);
                db.StaffingRateCards.Add(card);
            }
        }
        catch (ArgumentException ex) { throw Invalid("rateCard", ex.Message); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        await SaveAsync(ct);
        return ToDto(card);
    }

    public async Task<StaffingRateCardDto> SetRateCardStatusAsync(Guid id, RateCardStatusRequest r, CancellationToken ct)
    {
        var card = await FindAsync(db.StaffingRateCards, id, "Rate card", ct);
        if (r is null || r.ExpectedVersion != card.Version) throw Stale("rate card", card.Version);
        try { card.SetStatus(r.Status, Now); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        catch (ArgumentException ex) { throw Invalid("status", ex.Message); }
        await SaveAsync(ct);
        return ToDto(card);
    }

    public async Task<StaffingProposalDto> CreateProposalAsync(Guid dealId, CreateProposalRequest r, CancellationToken ct)
    {
        if (r is null) throw Invalid("body", "Request body is required.");
        var deal = await FindDealAsync(dealId, ct);
        var card = await FindAsync(db.StaffingRateCards, r.RateCardId, "Rate card", ct);
        if (card.Status != RateCardStatus.Active) throw new ConflictException("Activate the rate card before quoting from it.");
        if (card.ValidUntil is { } until && until < DateOnly.FromDateTime(Now)) throw new ConflictException("This rate card has expired.");
        var cardLines = Lines(card.LinesJson);
        var lines = r.Lines is { Count: > 0 } ? ValidLines(r.Lines) : cardLines;
        // Quoted rates come from the card: a role must exist on it, and its rate may only be kept or discounted.
        foreach (var line in lines)
        {
            var source = cardLines.FirstOrDefault(c => c.Role.Equals(line.Role, StringComparison.OrdinalIgnoreCase) && c.Seniority == line.Seniority && c.Unit == line.Unit);
            if (source is null) throw Invalid("lines", $"\"{line.Role}\" is not on rate card {card.Name} v{card.CardVersion}.");
            if (line.Rate > source.Rate) throw Invalid("lines", $"\"{line.Role}\" is quoted above the rate card ({source.Rate} {card.Currency}).");
        }
        StaffingProposal proposal;
        try
        {
            proposal = new StaffingProposal(user.OwnerId, dealId, card.Id, card.CardVersion, string.IsNullOrWhiteSpace(r.Title) ? $"Proposal for {deal.Title}" : r.Title,
                card.Currency, JsonSerializer.Serialize(lines, Json), card.Terms, r.Body, r.ValidUntil ?? card.ValidUntil, Now);
        }
        catch (ArgumentException ex) { throw Invalid("proposal", ex.Message); }
        db.StaffingProposals.Add(proposal);
        Log(deal, StaffingDealActivityType.ProposalChanged, $"Proposal drafted from rate card {card.Name} v{card.CardVersion} ({lines.Count} roles).");
        await SaveAsync(ct);
        return ToDto(proposal);
    }

    public Task<StaffingProposalDto> EditProposalAsync(Guid dealId, Guid id, EditProposalRequest r, CancellationToken ct) =>
        ProposalActionAsync(dealId, id, r?.ExpectedVersion, (deal, p) =>
        {
            p.Edit(r!.Title, p.Currency, JsonSerializer.Serialize(ValidLines(r.Lines), Json), r.Terms, r.Body, r.ValidUntil, Now);
            Log(deal, StaffingDealActivityType.ProposalChanged, "Proposal edited; approval cleared.");
        }, ct);

    public Task<StaffingProposalDto> ApproveProposalAsync(Guid dealId, Guid id, VersionRequest r, CancellationToken ct) =>
        ProposalActionAsync(dealId, id, null, (deal, p) =>
        {
            p.Approve(r?.ExpectedVersion ?? -1, Now);
            Log(deal, StaffingDealActivityType.ProposalChanged, $"Proposal approved (version {p.Version}).");
        }, ct);

    public Task<StaffingProposalDto> MarkProposalSentAsync(Guid dealId, Guid id, MarkHandoffRequest r, CancellationToken ct) =>
        ProposalActionAsync(dealId, id, null, (deal, p) =>
        {
            if (r is null) throw Invalid("body", "Request body is required.");
            p.MarkSent(r.ExpectedVersion, r.Receipt, Now);
            Log(deal, StaffingDealActivityType.ManualActionConfirmed, $"Proposal sent by {r.Channel}: {p.Receipt}");
        }, ct);

    public Task<StaffingProposalDto> RecordProposalAnswerAsync(Guid dealId, Guid id, ClientAnswerRequest r, CancellationToken ct) =>
        ProposalActionAsync(dealId, id, r?.ExpectedVersion, (deal, p) =>
        {
            p.RecordClientAnswer(r!.Accepted, Now);
            Log(deal, StaffingDealActivityType.ProposalChanged, $"Client {(r.Accepted ? "accepted" : "rejected")} the proposal.");
        }, ct);

    // ---------- KPIs (X14) ----------

    public async Task<StaffingKpisDto> KpisAsync(CancellationToken ct)
    {
        var now = Now;
        var deals = await Owned(db.StaffingDeals).ToListAsync(ct);
        var activities = await Owned(db.StaffingDealActivities).Where(a => a.Type == StaffingDealActivityType.StageChanged)
            .Select(a => new { a.DealId, a.Detail, a.OccurredAt }).ToListAsync(ct);
        var moves = activities.Select(a => (a.DealId, Next: ParseNext(a.Detail), a.OccurredAt)).Where(a => a.Next is not null).ToList();

        var funnel = StaffingKpiMath.Funnel;
        var reached = funnel.ToDictionary(s => s, s => deals.Count(d => StaffingKpiMath.Reached(d.Stage, d.StageBeforeHold, s) ||
            moves.Any(m => m.DealId == d.Id && m.Next == s)));
        var stages = Enum.GetValues<StaffingDealStage>()
            .Select(s => new StageCount(s, deals.Count(d => d.Stage == s), reached.GetValueOrDefault(s, deals.Count(d => d.Stage == s)))).ToList();
        var conversions = StaffingKpiMath.Steps.Select(step => new Conversion(step.From.ToString(), step.To.ToString(), reached[step.From], reached[step.To],
            reached[step.From] == 0 ? null : Math.Round((double)reached[step.To] / reached[step.From], 3))).ToList();

        var open = deals.Where(d => d.Stage is not (StaffingDealStage.Won or StaffingDealStage.Lost or StaffingDealStage.Disqualified)).ToList();
        var lastMove = moves.GroupBy(m => m.DealId).ToDictionary(g => g.Key, g => g.Max(m => m.OccurredAt));
        double? avgDays = open.Count == 0 ? null : Math.Round(open.Average(d => (now - lastMove.GetValueOrDefault(d.Id, d.CreatedAt)).TotalDays), 1);
        var replyHours = deals.Select(d =>
        {
            var contacted = moves.Where(m => m.DealId == d.Id && m.Next == StaffingDealStage.Contacted).Select(m => (DateTime?)m.OccurredAt).Min();
            var replied = moves.Where(m => m.DealId == d.Id && m.Next == StaffingDealStage.Replied).Select(m => (DateTime?)m.OccurredAt).Min();
            return contacted is { } c && replied is { } r && r >= c ? (r - c).TotalHours : (double?)null;
        }).Where(h => h is not null).Select(h => h!.Value).ToList();

        var candidates = await Owned(db.StaffingCandidates).Select(c => new { c.Consent, c.Availability }).ToListAsync(ct);
        var submissions = await Owned(db.StaffingSubmissions).Select(s => s.State).ToListAsync(ct);
        var interviews = await Owned(db.StaffingInterviews).Select(i => new { i.State, i.ScheduledAt, i.CandidateNotification }).ToListAsync(ct);
        var offers = await Owned(db.StaffingOffers).Select(o => new { o.State, o.Contract, o.Outcome, o.PlacementValue, o.Currency }).ToListAsync(ct);

        return new StaffingKpisDto(
            open.Count, deals.Count(d => d.Stage == StaffingDealStage.Won), deals.Count(d => d.Stage is StaffingDealStage.Lost or StaffingDealStage.Disqualified),
            stages, conversions,
            Enum.GetValues<StaffingDealSource>().Select(s => new SourceCount(s, deals.Count(d => d.Source == s), deals.Count(d => d.Source == s && d.Stage == StaffingDealStage.Won)))
                .Where(s => s.Deals > 0).ToList(),
            open.Where(d => d.EstimatedValue is not null && d.Currency is not null).GroupBy(d => d.Currency!)
                .Select(g => new MoneyTotal(g.Key, g.Sum(d => d.EstimatedValue!.Value))).OrderBy(m => m.Currency).ToList(),
            offers.Where(o => o.Outcome == PlacementOutcome.Placed && o.PlacementValue is not null && o.Currency is not null).GroupBy(o => o.Currency!)
                .Select(g => new MoneyTotal(g.Key, g.Sum(o => o.PlacementValue!.Value))).OrderBy(m => m.Currency).ToList(),
            avgDays, replyHours.Count == 0 ? null : Math.Round(replyHours.Average(), 1),
            open.Count(d => d.NextActionAt is { } due && due < now),
            candidates.Count, candidates.Count(c => c.Consent == CandidateConsent.Granted),
            candidates.Count(c => c.Availability is CandidateAvailability.Immediate or CandidateAvailability.NoticePeriod),
            submissions.Count(s => s == SubmissionState.Draft), submissions.Count(s => s == SubmissionState.Approved), submissions.Count(s => s == SubmissionState.Sent),
            interviews.Count(i => i.State == InterviewState.Scheduled && i.ScheduledAt >= now),
            interviews.Count(i => i.State == InterviewState.Completed),
            interviews.Count(i => i.State == InterviewState.Scheduled && i.CandidateNotification == NotificationStatus.NotNotified),
            offers.Count(o => o.State == OfferState.Extended), offers.Count(o => o.State == OfferState.Accepted),
            offers.Count(o => o.Contract == ContractStatus.Signed), offers.Count(o => o.Outcome == PlacementOutcome.Placed),
            await Owned(db.StaffingMessages).CountAsync(m => m.State == MessageState.Draft || m.State == MessageState.Approved, ct),
            await Owned(db.StaffingMessages).CountAsync(m => m.State == MessageState.Received && m.Intent == ReplyIntent.Unclassified, ct),
            await Owned(db.StaffingMeetings).CountAsync(m => m.State == MeetingState.Invited && m.StartsAt >= now, ct), now);
    }

    // ---------- helpers ----------

    private async Task<StaffingSubmissionDto> SubmissionActionAsync(Guid dealId, Guid id,
        Func<StaffingDeal, StaffingSubmission, StaffingCandidate, Task> act, CancellationToken ct)
    {
        var deal = await FindDealAsync(dealId, ct);
        var submission = await FindAsync(db.StaffingSubmissions, id, "Submission", ct);
        if (submission.DealId != dealId) throw new NotFoundException("Submission not found.");
        var candidate = await FindCandidateAsync(submission.CandidateId, ct);
        try { await act(deal, submission, candidate); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        await SaveAsync(ct);
        return ToDto(submission, candidate);
    }

    private async Task<StaffingInterviewDto> InterviewActionAsync(Guid dealId, Guid id, int? expectedVersion,
        Action<StaffingDeal, StaffingInterview> act, CancellationToken ct)
    {
        var deal = await FindDealAsync(dealId, ct);
        var interview = await FindAsync(db.StaffingInterviews, id, "Interview", ct);
        if (interview.DealId != dealId) throw new NotFoundException("Interview not found.");
        if (expectedVersion != interview.Version) throw Stale("interview", interview.Version);
        try { act(deal, interview); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        catch (ArgumentException ex) { throw Invalid("interview", ex.Message); }
        await SaveAsync(ct);
        return ToDto(interview);
    }

    private async Task<StaffingOfferDto> OfferActionAsync(Guid dealId, Guid id, int? expectedVersion,
        Action<StaffingDeal, StaffingOffer> act, CancellationToken ct)
    {
        var deal = await FindDealAsync(dealId, ct);
        var offer = await FindAsync(db.StaffingOffers, id, "Offer", ct);
        if (offer.DealId != dealId) throw new NotFoundException("Offer not found.");
        if (expectedVersion != offer.Version) throw Stale("offer", offer.Version);
        try { act(deal, offer); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        catch (ArgumentException ex) { throw Invalid("offer", ex.Message); }
        await SaveAsync(ct);
        return ToDto(offer);
    }

    private async Task<StaffingProposalDto> ProposalActionAsync(Guid dealId, Guid id, int? expectedVersion,
        Action<StaffingDeal, StaffingProposal> act, CancellationToken ct)
    {
        var deal = await FindDealAsync(dealId, ct);
        var proposal = await FindAsync(db.StaffingProposals, id, "Proposal", ct);
        if (proposal.DealId != dealId) throw new NotFoundException("Proposal not found.");
        if (expectedVersion is { } v && v != proposal.Version) throw Stale("proposal", proposal.Version);
        try { act(deal, proposal); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        catch (ArgumentException ex) { throw Invalid("proposal", ex.Message); }
        await SaveAsync(ct);
        return ToDto(proposal);
    }

    /// <summary>Moves the deal one step when a stored fact proves it, never skipping stages or reopening a closed deal.</summary>
    private void Advance(StaffingDeal deal, StaffingDealStage target)
    {
        if (deal.Stage == target || deal.Stage == StaffingDealStage.OnHold || !deal.CanMoveTo(target)) return;
        var previous = deal.Stage;
        deal.MoveTo(target, Now);
        Log(deal, StaffingDealActivityType.StageChanged, $"{previous} → {target}");
    }

    private void Log(StaffingDeal deal, StaffingDealActivityType type, string detail) =>
        db.StaffingDealActivities.Add(new StaffingDealActivity(user.OwnerId, deal.Id, type,
            detail.Length > StaffingDealActivity.MaxDetailLength ? detail[..(StaffingDealActivity.MaxDetailLength - 1)] + "…" : detail, Now));

    private static StaffingDealStage? ParseNext(string detail)
    {
        var arrow = detail.LastIndexOf('→');
        return arrow >= 0 && Enum.TryParse<StaffingDealStage>(detail[(arrow + 1)..].Trim(), out var s) ? s : null;
    }

    private static Dictionary<string, string?> Snapshot(StaffingCandidate c, CandidateField fields)
    {
        var s = new Dictionary<string, string?>();
        void Add(CandidateField f, string key, string? value) { if (fields.HasFlag(f)) s[key] = value; }
        Add(CandidateField.Name, "name", c.Name);
        Add(CandidateField.Headline, "headline", c.Headline);
        Add(CandidateField.Skills, "skills", c.Skills);
        Add(CandidateField.Experience, "yearsExperience", c.YearsExperience?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(CandidateField.Location, "location", c.Location);
        Add(CandidateField.Availability, "availability", c.Availability == CandidateAvailability.NoticePeriod && c.NoticePeriodDays is { } d
            ? $"{d} days' notice" : c.Availability.ToString());
        Add(CandidateField.Rate, "rate", c.RateAmount is { } a ? $"{a.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {c.RateCurrency} per {c.RateUnit?.ToString().ToLowerInvariant()}" : null);
        Add(CandidateField.Email, "email", c.Email);
        Add(CandidateField.Phone, "phone", c.Phone);
        Add(CandidateField.Resume, "resume", c.ResumeText is null ? null : $"Resume v{c.ResumeVersion}\n{c.ResumeText}");
        return s;
    }

    private static CandidateField ParseFields(IReadOnlyList<string>? names, string field)
    {
        var result = CandidateField.None;
        foreach (var name in names ?? [])
        {
            if (!Enum.TryParse<CandidateField>(name, ignoreCase: true, out var f) || f == CandidateField.None || (f & ~StaffingCandidate.AllFields) != 0)
                throw Invalid(field, $"Unknown field \"{name}\".");
            result |= f;
        }
        return result;
    }

    private static List<string> Names(CandidateField fields) =>
        Enum.GetValues<CandidateField>().Where(f => f != CandidateField.None && fields.HasFlag(f)).Select(f => f.ToString()).ToList();

    private static IReadOnlyList<RateCardLine> ValidLines(IReadOnlyList<RateCardLine>? lines)
    {
        if (lines is not { Count: > 0 }) throw Invalid("lines", "Add at least one role with a rate.");
        if (lines.Count > 100) throw Invalid("lines", "At most 100 roles.");
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Role) || l.Role.Length > 200) throw Invalid("lines", "Each role needs a name (up to 200 characters).");
            if (l.Seniority is { Length: > 100 }) throw Invalid("lines", "Seniority is at most 100 characters.");
            if (!Enum.IsDefined(l.Unit)) throw Invalid("lines", "Unknown rate unit.");
            if (l.Rate <= 0) throw Invalid("lines", $"\"{l.Role}\" needs a rate above zero.");
        }
        return lines.Select(l => l with { Role = l.Role.Trim(), Seniority = string.IsNullOrWhiteSpace(l.Seniority) ? null : l.Seniority.Trim() }).ToList();
    }

    private static IReadOnlyList<RateCardLine> Lines(string json) =>
        JsonSerializer.Deserialize<List<RateCardLine>>(json, Json) ?? [];

    private static bool ValidTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; } catch (TimeZoneNotFoundException) { return false; } catch (InvalidTimeZoneException) { return false; }
    }

    private IQueryable<T> Owned<T>(IQueryable<T> set) where T : class, Domain.Common.IOwned => set.Where(x => x.OwnerId == user.OwnerId);

    private async Task<T> FindAsync<T>(DbSet<T> set, Guid id, string what, CancellationToken ct) where T : class, Domain.Common.IOwned =>
        await set.Where(x => x.OwnerId == user.OwnerId).FirstOrDefaultAsync(x => EF.Property<Guid>(x, "Id") == id, ct)
        ?? throw new NotFoundException($"{what} not found.");

    private Task<StaffingCandidate> FindCandidateAsync(Guid id, CancellationToken ct) => FindAsync(db.StaffingCandidates, id, "Candidate", ct);
    private Task<StaffingDeal> FindDealAsync(Guid id, CancellationToken ct) => FindAsync(db.StaffingDeals, id, "Staffing deal", ct);

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("This changed elsewhere. Reload before saving."); }
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static ConflictException Stale(string what, int version) => new($"The {what} changed elsewhere (now version {version}). Reload before saving.");

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private static StaffingCandidateDto ToDto(StaffingCandidate c) =>
        new(c.Id, c.Name, c.Headline, c.Email, c.Phone, c.Location, c.Skills, c.YearsExperience, c.Availability, c.NoticePeriodDays,
            c.RateAmount, c.RateCurrency, c.RateUnit, c.ResumeText is not null, c.ResumeVersion, c.Consent, c.ConsentRecordedAt,
            c.ConsentEvidence, Names(c.ShareableFields), c.NotifyByEmail, c.Version, c.CreatedAt, c.UpdatedAt);

    private static StaffingSubmissionDto ToDto(StaffingSubmission s, StaffingCandidate? c) =>
        new(s.Id, s.DealId, s.CandidateId, c?.Name ?? "Removed candidate", Names(s.SharedFields),
            JsonSerializer.Deserialize<Dictionary<string, string?>>(s.SnapshotJson, Json) ?? [], s.CandidateVersion, s.ResumeVersion,
            c is not null && c.Version != s.CandidateVersion, s.Note, s.State, s.ApprovedAt, s.Channel, s.Receipt, s.SentAt, s.Version,
            s.CreatedAt, s.UpdatedAt);

    private static StaffingInterviewDto ToDto(StaffingInterview i) =>
        new(i.Id, i.DealId, i.SubmissionId, i.Round, i.State, i.ScheduledAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            i.TimeZone, i.DurationMinutes, i.Mode, i.Location, i.CandidateNotes, i.InternalNotes, i.CandidateNotification,
            i.CandidateNotifiedAt, i.Version, i.UpdatedAt);

    private static StaffingFeedbackDto ToDto(StaffingFeedback f) =>
        new(f.Id, f.DealId, f.SubmissionId, f.InterviewId, f.Source, f.Decision, f.Detail, f.SharedWithCandidate, f.RecordedAt);

    private static StaffingOfferDto ToDto(StaffingOffer o) =>
        new(o.Id, o.DealId, o.SubmissionId, o.ClientRate, o.CandidatePay, o.Currency, o.Unit, o.StartDate, o.PlacementValue, o.State,
            o.Contract, o.ContractVersion, o.SignatureProvider, o.SignedDocumentReference is not null, o.Outcome, o.Notes, o.Version, o.UpdatedAt);

    private static StaffingRateCardDto ToDto(StaffingRateCard r) =>
        new(r.Id, r.Name, r.Currency, Lines(r.LinesJson), r.Terms, r.ValidUntil, r.CardVersion, r.Status, r.Version, r.UpdatedAt);

    private static StaffingProposalDto ToDto(StaffingProposal p) =>
        new(p.Id, p.DealId, p.RateCardId, p.RateCardVersion, p.Title, p.Currency, Lines(p.LinesJson), p.Terms, p.Body, p.ValidUntil,
            p.State, p.ApprovedAt, p.Receipt, p.SentAt, p.Version, p.UpdatedAt);
}

/// <summary>The forward pipeline order used by the funnel and conversion KPIs.</summary>
public static class StaffingKpiMath
{
    public static readonly StaffingDealStage[] Funnel =
    [
        StaffingDealStage.New, StaffingDealStage.Qualified, StaffingDealStage.Shortlisted, StaffingDealStage.OutreachApproved,
        StaffingDealStage.Contacted, StaffingDealStage.Replied, StaffingDealStage.MeetingScheduled, StaffingDealStage.RequirementConfirmed,
        StaffingDealStage.CandidatesSubmitted, StaffingDealStage.Interviewing, StaffingDealStage.Offer, StaffingDealStage.Contracting,
        StaffingDealStage.Won
    ];

    public static readonly (StaffingDealStage From, StaffingDealStage To)[] Steps =
    [
        (StaffingDealStage.Contacted, StaffingDealStage.Replied),
        (StaffingDealStage.Replied, StaffingDealStage.MeetingScheduled),
        (StaffingDealStage.MeetingScheduled, StaffingDealStage.CandidatesSubmitted),
        (StaffingDealStage.CandidatesSubmitted, StaffingDealStage.Interviewing),
        (StaffingDealStage.Interviewing, StaffingDealStage.Offer),
        (StaffingDealStage.Offer, StaffingDealStage.Won)
    ];

    /// <summary>A deal at stage S (or on hold from S) has passed every earlier stage of the forward funnel.</summary>
    public static bool Reached(StaffingDealStage current, StaffingDealStage? beforeHold, StaffingDealStage stage)
    {
        var effective = current == StaffingDealStage.OnHold && beforeHold is { } b ? b : current;
        var at = Array.IndexOf(Funnel, effective);
        var target = Array.IndexOf(Funnel, stage);
        return at >= 0 && target >= 0 && at >= target;
    }
}
