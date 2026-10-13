using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Staffing;

namespace OpportunityPilot.Api.Controllers;

/// <summary>Candidates, submissions, interviews, feedback, offers, rate cards, proposals and KPIs of the staffing CRM.</summary>
[ApiController]
[Route("api/v1/staffing")]
public sealed class StaffingPipelineController(StaffingPipelineService pipeline, ProgressLinkService progress) : ControllerBase
{
    [HttpGet("candidates/{id:guid}/progress-link")]
    public async Task<ActionResult<ProgressLinkDto>> ProgressLink(Guid id, CancellationToken ct) => Ok(await progress.GetAsync(id, ct));

    [HttpPost("candidates/{id:guid}/progress-link")]
    public async Task<ActionResult<ProgressLinkDto>> CreateProgressLink(Guid id, CreateProgressLinkRequest request, CancellationToken ct) => Ok(await progress.CreateAsync(id, request, ct));

    [HttpDelete("candidates/{id:guid}/progress-link")]
    public async Task<IActionResult> RevokeProgressLink(Guid id, CancellationToken ct) { await progress.RevokeAsync(id, ct); return NoContent(); }

    [HttpGet("candidates")]
    public async Task<ActionResult<IReadOnlyList<StaffingCandidateDto>>> Candidates(CancellationToken ct) => Ok(await pipeline.ListCandidatesAsync(ct));

    [HttpGet("candidates/{id:guid}")]
    public async Task<ActionResult<StaffingCandidateDto>> Candidate(Guid id, CancellationToken ct) => Ok(await pipeline.GetCandidateAsync(id, ct));

    [HttpPost("candidates")]
    public async Task<ActionResult<StaffingCandidateDto>> CreateCandidate(SaveStaffingCandidateRequest request, CancellationToken ct)
    {
        var created = await pipeline.SaveCandidateAsync(null, request, ct);
        return CreatedAtAction(nameof(Candidate), new { id = created.Id }, created);
    }

    [HttpPut("candidates/{id:guid}")]
    public async Task<ActionResult<StaffingCandidateDto>> UpdateCandidate(Guid id, SaveStaffingCandidateRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveCandidateAsync(id, request, ct));

    [HttpPost("candidates/{id:guid}/consent")]
    public async Task<ActionResult<StaffingCandidateDto>> Consent(Guid id, RecordConsentRequest request, CancellationToken ct) =>
        Ok(await pipeline.RecordConsentAsync(id, request, ct));

    [HttpGet("deals/{dealId:guid}/work")]
    public async Task<ActionResult<StaffingDealWorkDto>> Work(Guid dealId, CancellationToken ct) => Ok(await pipeline.DealWorkAsync(dealId, ct));

    [HttpPost("deals/{dealId:guid}/submissions")]
    public async Task<ActionResult<StaffingSubmissionDto>> CreateSubmission(Guid dealId, SaveSubmissionRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveSubmissionAsync(dealId, null, request, ct));

    [HttpPut("deals/{dealId:guid}/submissions/{id:guid}")]
    public async Task<ActionResult<StaffingSubmissionDto>> UpdateSubmission(Guid dealId, Guid id, SaveSubmissionRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveSubmissionAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/submissions/{id:guid}/approve")]
    public async Task<ActionResult<StaffingSubmissionDto>> ApproveSubmission(Guid dealId, Guid id, VersionRequest request, CancellationToken ct) =>
        Ok(await pipeline.ApproveSubmissionAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/submissions/{id:guid}/sent")]
    public async Task<ActionResult<StaffingSubmissionDto>> SubmissionSent(Guid dealId, Guid id, MarkHandoffRequest request, CancellationToken ct) =>
        Ok(await pipeline.MarkSubmissionSentAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/submissions/{id:guid}/withdraw")]
    public async Task<ActionResult<StaffingSubmissionDto>> WithdrawSubmission(Guid dealId, Guid id, CancellationToken ct) =>
        Ok(await pipeline.WithdrawSubmissionAsync(dealId, id, ct));

    [HttpPost("deals/{dealId:guid}/interviews")]
    public async Task<ActionResult<StaffingInterviewDto>> RequestInterview(Guid dealId, RequestInterviewRequest request, CancellationToken ct) =>
        Ok(await pipeline.RequestInterviewAsync(dealId, request, ct));

    [HttpPost("deals/{dealId:guid}/interviews/{id:guid}/schedule")]
    public async Task<ActionResult<StaffingInterviewDto>> Schedule(Guid dealId, Guid id, ScheduleInterviewRequest request, CancellationToken ct) =>
        Ok(await pipeline.ScheduleInterviewAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/interviews/{id:guid}/finish")]
    public async Task<ActionResult<StaffingInterviewDto>> Finish(Guid dealId, Guid id, FinishInterviewRequest request, CancellationToken ct) =>
        Ok(await pipeline.FinishInterviewAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/interviews/{id:guid}/notified")]
    public async Task<ActionResult<StaffingInterviewDto>> Notified(Guid dealId, Guid id, NotifyCandidateRequest request, CancellationToken ct) =>
        Ok(await pipeline.RecordCandidateNotifiedAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/feedback")]
    public async Task<ActionResult<StaffingFeedbackDto>> Feedback(Guid dealId, RecordFeedbackRequest request, CancellationToken ct) =>
        Ok(await pipeline.RecordFeedbackAsync(dealId, request, ct));

    [HttpPost("deals/{dealId:guid}/offers")]
    public async Task<ActionResult<StaffingOfferDto>> CreateOffer(Guid dealId, SaveOfferRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveOfferAsync(dealId, null, request, ct));

    [HttpPut("deals/{dealId:guid}/offers/{id:guid}")]
    public async Task<ActionResult<StaffingOfferDto>> UpdateOffer(Guid dealId, Guid id, SaveOfferRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveOfferAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/offers/{id:guid}/state")]
    public async Task<ActionResult<StaffingOfferDto>> MoveOffer(Guid dealId, Guid id, MoveOfferRequest request, CancellationToken ct) =>
        Ok(await pipeline.MoveOfferAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/offers/{id:guid}/contract")]
    public async Task<ActionResult<StaffingOfferDto>> Contract(Guid dealId, Guid id, UpdateContractRequest request, CancellationToken ct) =>
        Ok(await pipeline.UpdateContractAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/offers/{id:guid}/outcome")]
    public async Task<ActionResult<StaffingOfferDto>> Outcome(Guid dealId, Guid id, RecordOutcomeRequest request, CancellationToken ct) =>
        Ok(await pipeline.RecordOutcomeAsync(dealId, id, request, ct));

    [HttpGet("rate-cards")]
    public async Task<ActionResult<IReadOnlyList<StaffingRateCardDto>>> RateCards(CancellationToken ct) => Ok(await pipeline.ListRateCardsAsync(ct));

    [HttpPost("rate-cards")]
    public async Task<ActionResult<StaffingRateCardDto>> CreateRateCard(SaveRateCardRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveRateCardAsync(null, request, ct));

    [HttpPut("rate-cards/{id:guid}")]
    public async Task<ActionResult<StaffingRateCardDto>> UpdateRateCard(Guid id, SaveRateCardRequest request, CancellationToken ct) =>
        Ok(await pipeline.SaveRateCardAsync(id, request, ct));

    [HttpPost("rate-cards/{id:guid}/status")]
    public async Task<ActionResult<StaffingRateCardDto>> RateCardStatus(Guid id, RateCardStatusRequest request, CancellationToken ct) =>
        Ok(await pipeline.SetRateCardStatusAsync(id, request, ct));

    [HttpPost("deals/{dealId:guid}/proposals")]
    public async Task<ActionResult<StaffingProposalDto>> CreateProposal(Guid dealId, CreateProposalRequest request, CancellationToken ct) =>
        Ok(await pipeline.CreateProposalAsync(dealId, request, ct));

    [HttpPut("deals/{dealId:guid}/proposals/{id:guid}")]
    public async Task<ActionResult<StaffingProposalDto>> EditProposal(Guid dealId, Guid id, EditProposalRequest request, CancellationToken ct) =>
        Ok(await pipeline.EditProposalAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/proposals/{id:guid}/approve")]
    public async Task<ActionResult<StaffingProposalDto>> ApproveProposal(Guid dealId, Guid id, VersionRequest request, CancellationToken ct) =>
        Ok(await pipeline.ApproveProposalAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/proposals/{id:guid}/sent")]
    public async Task<ActionResult<StaffingProposalDto>> ProposalSent(Guid dealId, Guid id, MarkHandoffRequest request, CancellationToken ct) =>
        Ok(await pipeline.MarkProposalSentAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/proposals/{id:guid}/answer")]
    public async Task<ActionResult<StaffingProposalDto>> ProposalAnswer(Guid dealId, Guid id, ClientAnswerRequest request, CancellationToken ct) =>
        Ok(await pipeline.RecordProposalAnswerAsync(dealId, id, request, ct));

    [HttpGet("deals/{dealId:guid}/conversation")]
    public async Task<ActionResult<StaffingConversationDto>> Conversation(Guid dealId, CancellationToken ct) =>
        Ok(await pipeline.ConversationAsync(dealId, ct));

    [HttpPost("deals/{dealId:guid}/messages")]
    public async Task<ActionResult<StaffingMessageDto>> DraftMessage(Guid dealId, DraftMessageRequest request, CancellationToken ct) =>
        Ok(await pipeline.DraftMessageAsync(dealId, request, ct));

    [HttpPut("deals/{dealId:guid}/messages/{id:guid}")]
    public async Task<ActionResult<StaffingMessageDto>> EditMessage(Guid dealId, Guid id, EditMessageRequest request, CancellationToken ct) =>
        Ok(await pipeline.EditMessageAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/messages/{id:guid}/approve")]
    public async Task<ActionResult<StaffingMessageDto>> ApproveMessage(Guid dealId, Guid id, VersionRequest request, CancellationToken ct) =>
        Ok(await pipeline.ApproveMessageAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/messages/{id:guid}/sent")]
    public async Task<ActionResult<StaffingMessageDto>> MessageSent(Guid dealId, Guid id, MessageSentRequest request, CancellationToken ct) =>
        Ok(await pipeline.MarkMessageSentAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/replies")]
    public async Task<ActionResult<StaffingMessageDto>> RecordReply(Guid dealId, RecordReplyRequest request, CancellationToken ct) =>
        Ok(await pipeline.RecordReplyAsync(dealId, request, ct));

    [HttpPost("deals/{dealId:guid}/messages/{id:guid}/intent")]
    public async Task<ActionResult<StaffingMessageDto>> ClassifyReply(Guid dealId, Guid id, ClassifyReplyRequest request, CancellationToken ct) =>
        Ok(await pipeline.ClassifyReplyAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/meetings")]
    public async Task<ActionResult<StaffingMeetingDto>> PlanMeeting(Guid dealId, PlanMeetingRequest request, CancellationToken ct) =>
        Ok(await pipeline.PlanMeetingAsync(dealId, null, request, ct));

    [HttpPut("deals/{dealId:guid}/meetings/{id:guid}")]
    public async Task<ActionResult<StaffingMeetingDto>> ReplanMeeting(Guid dealId, Guid id, PlanMeetingRequest request, CancellationToken ct) =>
        Ok(await pipeline.PlanMeetingAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/meetings/{id:guid}/approve")]
    public async Task<ActionResult<StaffingMeetingDto>> ApproveMeeting(Guid dealId, Guid id, VersionRequest request, CancellationToken ct) =>
        Ok(await pipeline.ApproveMeetingAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/meetings/{id:guid}/invited")]
    public async Task<ActionResult<StaffingMeetingDto>> MeetingInvited(Guid dealId, Guid id, MeetingInvitedRequest request, CancellationToken ct) =>
        Ok(await pipeline.MarkMeetingInvitedAsync(dealId, id, request, ct));

    [HttpPost("deals/{dealId:guid}/meetings/{id:guid}/finish")]
    public async Task<ActionResult<StaffingMeetingDto>> FinishMeeting(Guid dealId, Guid id, FinishMeetingRequest request, CancellationToken ct) =>
        Ok(await pipeline.FinishMeetingAsync(dealId, id, request, ct));

    [HttpGet("kpis")]
    public async Task<ActionResult<StaffingKpisDto>> Kpis(CancellationToken ct) => Ok(await pipeline.KpisAsync(ct));
}
