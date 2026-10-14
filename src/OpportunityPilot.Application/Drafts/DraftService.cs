using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Analytics;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Drafts;

public sealed class DraftService(IAppDbContext db, ICurrentUser user, TimeProvider clock, UserEventLog events)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DraftDto> CreateAsync(Guid opportunityId, CreateDraftRequest request, CancellationToken ct)
    {
        if (request is null || !Enum.IsDefined(request.Channel))
            throw Invalid("channel", "Unknown draft channel.");

        var ownerId = user.OwnerId;
        var opportunity = await db.Opportunities.FirstOrDefaultAsync(o => o.Id == opportunityId && o.OwnerId == ownerId, ct)
            ?? throw new NotFoundException("Opportunity not found.");
        if (request.Channel == DraftChannel.CoverNote && opportunity.Mode != OpportunityMode.Job)
            throw Invalid("channel", "Cover notes can only be created for Job opportunities.");
        if (await db.OutreachDrafts.AnyAsync(d => d.OwnerId == ownerId && d.OpportunityId == opportunityId && d.Channel == request.Channel, ct))
            throw new ConflictException($"This opportunity already has a {ChannelName(request.Channel)} draft. Edit the existing draft instead.");
        var recipient = NormalizeRecipient(request.Channel, request.Recipient);
        await EnsureNotSuppressedAsync(recipient, ct);

        var campaign = await db.Campaigns.FirstAsync(c => c.Id == opportunity.CampaignId && c.OwnerId == ownerId, ct);
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.Id == campaign.ProfileId && p.OwnerId == ownerId, ct)
            ?? throw new NotFoundException("Campaign profile not found.");
        if (profile.ConfirmedAt is null)
            throw Invalid("profile", "Confirm the campaign profile before generating a cover note.");

        var generated = request.Channel == DraftChannel.CoverNote
            ? GenerateCoverNote(opportunity, campaign.CriteriaJson, profile.StructuredDataJson)
            : GenerateOutreach(opportunity, profile.StructuredDataJson, request.Channel);
        // Every claim taken from the profile names the exact version it came from (P2).
        generated = (generated.Body, generated.Claims.Select(c => c.Basis == "Profile" ? c with { Basis = $"Profile v{profile.Version}" } : c).ToList());
        var now = clock.GetUtcNow().UtcDateTime;
        var recipientEvidence = RecipientEvidence(recipient, opportunity.FactsJson);
        var draft = new OutreachDraft(ownerId, opportunity.Id, request.Channel, recipient, recipientEvidence is not null, null,
            generated.Body, DraftSource.Template, JsonSerializer.Serialize(generated.Claims, Json), now);
        db.OutreachDrafts.Add(draft);
        events.Add(UserEventKinds.DraftCreated, $"Wrote {WithArticle(ChannelName(request.Channel))} draft", opportunity.Id, opportunity.Title);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException($"This opportunity already has a {ChannelName(request.Channel)} draft. Reload to see it.");
        }
        return ToDto(draft, opportunity.FactsJson);
    }

    public async Task<IReadOnlyList<DraftDto>> ListForOpportunityAsync(Guid opportunityId, CancellationToken ct)
    {
        await EnsureOpportunityAsync(opportunityId, ct);
        var rows = await Owned.Where(d => d.OpportunityId == opportunityId)
            .OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id).ToListAsync(ct);
        var facts = await db.Opportunities.Where(o => o.Id == opportunityId && o.OwnerId == user.OwnerId)
            .Select(o => o.FactsJson).SingleAsync(ct);
        return rows.Select(d => ToDto(d, facts)).ToList();
    }

    public async Task<DraftPageDto> ListAsync(DraftState? state, DraftChannel? channel, Guid? campaignId, int take, int skip, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var query = Owned;
        if (state is { } value) query = query.Where(d => d.State == value);
        if (channel is { } requestedChannel) query = query.Where(d => d.Channel == requestedChannel);
        var joined = from draft in query
                     join opportunity in db.Opportunities on draft.OpportunityId equals opportunity.Id
                     join campaign in db.Campaigns on opportunity.CampaignId equals campaign.Id
                     where opportunity.OwnerId == user.OwnerId && campaign.OwnerId == user.OwnerId
                         && (campaignId == null || campaign.Id == campaignId)
                     select new { draft, opportunity.Title, opportunity.Organization, opportunity.FactsJson, CampaignId = campaign.Id, CampaignName = campaign.Name };
        var total = await joined.CountAsync(ct);
        var rows = await joined.OrderByDescending(x => x.draft.UpdatedAt).ThenBy(x => x.draft.Id)
            .Skip(skip).Take(take).ToListAsync(ct);
        return new DraftPageDto(total, rows.Select(x => new DraftListItemDto(
            x.draft.Id, x.draft.OpportunityId, x.CampaignId, x.CampaignName, x.Title, x.Organization,
            x.draft.Channel, x.draft.Recipient, x.draft.RecipientVerified,
            x.draft.RecipientVerified ? "Evidence" : "UserEntered", RecipientEvidence(x.draft.Recipient, x.FactsJson),
            x.draft.State == DraftState.Sent ? DraftState.Sent : x.draft.HasValidApproval() ? DraftState.Approved : DraftState.Draft, x.draft.Version, x.draft.UpdatedAt)).ToList());
    }

    public async Task<DraftDto> GetAsync(Guid id, CancellationToken ct)
    {
        var draft = await FindAsync(id, ct);
        return ToDto(draft, await OpportunityFactsAsync(draft.OpportunityId, ct));
    }

    public async Task<DraftDto> UpdateAsync(Guid id, UpdateDraftRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        if (string.IsNullOrWhiteSpace(request.Body)) throw Invalid("body", "Draft body is required.");
        if (request.Body.Length > OutreachDraft.MaxBodyLength)
            throw Invalid("body", $"Draft body must be at most {OutreachDraft.MaxBodyLength} characters.");
        if (request.Recipient?.Trim().Length > OutreachDraft.MaxRecipientLength)
            throw Invalid("recipient", $"Recipient must be at most {OutreachDraft.MaxRecipientLength} characters.");
        if (request.Subject?.Trim().Length > OutreachDraft.MaxSubjectLength)
            throw Invalid("subject", $"Subject must be at most {OutreachDraft.MaxSubjectLength} characters.");

        var draft = await FindAsync(id, ct);
        if (draft.Version != request.ExpectedVersion)
            throw new ConflictException($"Draft was changed elsewhere (now version {draft.Version}). Reload before saving.");
        var recipient = NormalizeRecipient(draft.Channel, request.Recipient);
        await EnsureNotSuppressedAsync(recipient, ct);
        try
        {
            var verified = RecipientEvidence(recipient, await OpportunityFactsAsync(draft.OpportunityId, ct)) is not null;
            draft.Update(recipient, verified, request.Subject, request.Body, clock.GetUtcNow().UtcDateTime);
            await LogDraftAsync(UserEventKinds.DraftEdited, $"Edited the {ChannelName(draft.Channel)} draft (version {draft.Version})", draft.OpportunityId, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (ArgumentException ex) { throw Invalid("body", ex.Message); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Draft was changed elsewhere. Reload before saving."); }
        return ToDto(draft, await OpportunityFactsAsync(draft.OpportunityId, ct));
    }

    public async Task<DraftDto> ApproveAsync(Guid id, ApproveDraftRequest request, CancellationToken ct)
    {
        var draft = await FindAsync(id, ct);
        if (request is null || request.Version != draft.Version)
            throw new ConflictException($"Draft was changed elsewhere (now version {draft.Version}). Reload before approving.");
        await EnsureNotSuppressedAsync(draft.Recipient, ct);
        if (ContainsUnresolvedPlaceholder(draft.Body) || ContainsUnresolvedPlaceholder(draft.Subject))
            throw Invalid("draft", "Replace every [placeholder] before approving this draft.");
        try
        {
            draft.Approve(request.Version, clock.GetUtcNow().UtcDateTime);
            await LogDraftAsync(UserEventKinds.DraftApproved, $"Approved the {ChannelName(draft.Channel)} draft (version {draft.Version})", draft.OpportunityId, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (InvalidOperationException ex) { throw Invalid("draft", ex.Message); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Draft was changed elsewhere. Reload before approving."); }
        return ToDto(draft, await OpportunityFactsAsync(draft.OpportunityId, ct));
    }

    public async Task<IReadOnlyList<BatchApproveDraftResult>> BatchApproveAsync(BatchApproveDraftRequest request, CancellationToken ct)
    {
        if (request?.Items is null || request.Items.Count == 0) throw Invalid("items", "At least one draft is required.");
        if (request.Items.Count > 200) throw Invalid("items", "At most 200 drafts can be approved at once.");
        var results = new List<BatchApproveDraftResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            try
            {
                var draft = await ApproveAsync(item.Id, new ApproveDraftRequest(item.Version), ct);
                results.Add(new(item.Id, true, null, draft));
            }
            catch (Exception ex) when (ex is NotFoundException or ConflictException or RequestValidationException)
            {
                db.ChangeTracker.Clear();
                results.Add(new(item.Id, false, ex.Message, null));
            }
        }
        return results;
    }

    private static string WithArticle(string noun) => noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? $"an {noun}" : $"a {noun}";

    private async Task LogDraftAsync(string kind, string detail, Guid opportunityId, CancellationToken ct)
    {
        var title = await db.Opportunities.AsNoTracking().Where(o => o.Id == opportunityId && o.OwnerId == user.OwnerId).Select(o => o.Title).FirstOrDefaultAsync(ct);
        events.Add(kind, detail, opportunityId, title);
    }

    public async Task<DraftDto> RevokeAsync(Guid id, CancellationToken ct)
    {
        var draft = await FindAsync(id, ct);
        draft.RevokeApproval(clock.GetUtcNow().UtcDateTime);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Draft was changed elsewhere. Reload and try again."); }
        return ToDto(draft, await OpportunityFactsAsync(draft.OpportunityId, ct));
    }

    /// <summary>"Edit and send again": a sent draft becomes a new editable version. It needs a new approval and a new send record.</summary>
    public async Task<DraftDto> ReopenAsync(Guid id, CancellationToken ct)
    {
        var draft = await FindAsync(id, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        try { draft.Reopen(now); }
        catch (InvalidOperationException ex) { throw new ConflictException(ex.Message); }
        db.Activities.Add(new Activity(user.OwnerId, draft.OpportunityId, "Note", now,
            $"{draft.Channel} message reopened to edit and send again (new version {draft.Version}). The earlier send stays on record."));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Draft was changed elsewhere. Reload and try again."); }
        return ToDto(draft, await OpportunityFactsAsync(draft.OpportunityId, ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        db.OutreachDrafts.Remove(await FindAsync(id, ct));
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<OutreachDraft> Owned => db.OutreachDrafts.Where(d => d.OwnerId == user.OwnerId);

    public Task<int> CountAwaitingReviewAsync(CancellationToken ct) => Owned.CountAsync(d => d.State == DraftState.Draft, ct);

    public Task<int> CountApprovedAsync(CancellationToken ct) => Owned.CountAsync(d => d.State == DraftState.Approved, ct);

    /// <summary>Drafts that have been sent at least once (a reopened draft keeps its last send time).</summary>
    public Task<int> CountSentAsync(CancellationToken ct) => Owned.CountAsync(d => d.SentAt != null, ct);

    private async Task<OutreachDraft> FindAsync(Guid id, CancellationToken ct) =>
        await Owned.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Draft not found.");

    private async Task EnsureOpportunityAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Opportunities.AnyAsync(o => o.Id == id && o.OwnerId == user.OwnerId, ct))
            throw new NotFoundException("Opportunity not found.");
    }

    public static DraftDto ToDto(OutreachDraft draft, string? factsJson = null)
    {
        var validApproval = draft.HasValidApproval();
        var state = draft.State == DraftState.Sent ? DraftState.Sent : validApproval ? DraftState.Approved : DraftState.Draft;
        var blockers = new List<string> { "Sending requires a configured provider or manual copy." };
        if (draft.Channel == DraftChannel.Email && string.IsNullOrWhiteSpace(draft.Recipient)) blockers.Add("Email drafts require a recipient.");
        if (!string.IsNullOrWhiteSpace(draft.Recipient) && !draft.RecipientVerified)
            blockers.Add("Recipient was entered manually and has not been verified against evidence.");
        if (ContainsUnresolvedPlaceholder(draft.Body) || ContainsUnresolvedPlaceholder(draft.Subject))
            blockers.Add("Replace every [placeholder] before approval.");
        if (!validApproval) blockers.Add("Approve this exact draft before the agent can use it.");
        var evidenceId = RecipientEvidence(draft.Recipient, factsJson);
        return new DraftDto(draft.Id, draft.OpportunityId, draft.Channel, draft.Recipient, draft.RecipientVerified,
            draft.RecipientVerified ? "Evidence" : "UserEntered", evidenceId, draft.Subject, draft.Body, draft.Version, state, validApproval ? draft.ApprovedVersion : null,
            validApproval ? draft.ApprovedAt : null, draft.Source, null, Claims(draft.ClaimsJson),
            false, blockers, draft.CreatedAt, draft.UpdatedAt);
    }

    private static (string Body, IReadOnlyList<DraftClaimDto> Claims) GenerateOutreach(
        Opportunity opportunity, string profileJson, DraftChannel channel)
    {
        var profile = Object(profileJson);
        var offer = String(profile, "offer", "summary", "professionalSummary") ?? "[Add your confirmed offer here.]";
        var organization = string.IsNullOrWhiteSpace(opportunity.Organization) ? "[organization]" : opportunity.Organization;
        // Sales profiles carry an outreach identity (name, role, company, signature); candidates their name.
        var name = String(profile, "outreachIdentity", "fullName", "candidateName", "name") ?? "[Your name]";
        var claims = offer.StartsWith('[') ? new List<DraftClaimDto>() : [new DraftClaimDto(offer, "Profile", null)];
        var full = $"Hello {organization},\n\nI noticed {opportunity.Title}. {offer}\n\nWould a short conversation be useful?\n\nRegards,\n{name}";
        var body = channel == DraftChannel.LinkedInMessage ? LinkedInNote.Build(organization, opportunity.Title, offer, name) : full;
        return (body, claims);
    }

    private static (string Body, IReadOnlyList<DraftClaimDto> Claims) GenerateCoverNote(
        Opportunity opportunity, string criteriaJson, string profileJson)
    {
        var profile = Object(profileJson);
        var facts = Deserialize<List<FactRow>>(opportunity.FactsJson) ?? [];
        var breakdown = Deserialize<List<BreakdownRow>>(opportunity.BreakdownJson) ?? [];
        var criteria = CampaignCriteria.FromJson(criteriaJson);
        var claims = new List<DraftClaimDto>();
        var evidence = facts.FirstOrDefault(f => !f.IsInference && Guid.TryParse(f.EvidenceId, out _))?.EvidenceId
            ?? breakdown.SelectMany(b => b.EvidenceIds).FirstOrDefault(id => Guid.TryParse(id, out _));
        Guid? EvidenceId(string? raw) => Guid.TryParse(raw, out var id) ? id : null;

        var verifiedOrganization = facts.FirstOrDefault(f => f.Key == "organization" && !f.IsInference);
        var organization = verifiedOrganization?.Value?.Trim();
        var greeting = string.IsNullOrWhiteSpace(organization)
            ? "Dear Hiring Manager,"
            : $"Dear Hiring Team at {organization},";

        var title = opportunity.Title.Trim();
        var intro = $"I am applying for the {title} role.";
        if (EvidenceId(evidence) is { } titleEvidence) claims.Add(new DraftClaimDto(intro, "Evidence", titleEvidence));

        var paragraphs = new List<string> { greeting, string.Empty, intro };
        var skillFact = facts.FirstOrDefault(f => f.Key == "requiredSkillsFound" && !f.IsInference);
        if (skillFact is not null && !string.IsNullOrWhiteSpace(skillFact.Value))
        {
            var sentence = $"The role calls for skills I match: {skillFact.Value}.";
            paragraphs.Add(sentence);
            if (EvidenceId(skillFact.EvidenceId) is { } skillEvidence) claims.Add(new DraftClaimDto(sentence, "Evidence", skillEvidence));
        }

        if (criteria.CandidateYears is { } years)
        {
            var text = years.ToString("0.#", CultureInfo.InvariantCulture);
            var sentence = $"My confirmed experience for this campaign is {text} years.";
            paragraphs.Add(sentence);
            claims.Add(new DraftClaimDto(sentence, "Profile", null));
        }

        if (String(profile, "offer", "summary", "professionalSummary") is { } offer)
        {
            paragraphs.Add(offer);
            claims.Add(new DraftClaimDto(offer, "Profile", null));
        }
        else paragraphs.Add("[Add your confirmed professional summary here.] ");

        if (String(profile, "availability", "noticePeriod", "startDate") is { } availability)
        {
            var sentence = $"Availability: {availability}";
            paragraphs.Add(sentence);
            claims.Add(new DraftClaimDto(sentence, "Profile", null));
        }

        paragraphs.AddRange([string.Empty, "Thank you for considering my application.", string.Empty,
            "Sincerely,", String(profile, "fullName", "candidateName", "name") ?? "[Your name]"]);
        return (string.Join(Environment.NewLine, paragraphs), claims);
    }

    private static JsonElement Object(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : default;
        }
        catch (JsonException) { return default; }
    }

    private static string? String(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var candidate in new[] { root }.Concat(
                     root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object ? [fields] : []))
            foreach (var name in names)
                if (candidate.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!.Trim();
        return null;
    }

    private static IReadOnlyList<DraftClaimDto> Claims(string json) => Deserialize<List<DraftClaimDto>>(json) ?? [];

    private async Task EnsureNotSuppressedAsync(string? recipient, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient)) return;
        var normalized = Domain.Outreach.Suppression.Normalize(recipient);
        if (await db.Suppressions.AnyAsync(x => x.OwnerId == user.OwnerId && x.NormalizedRecipient == normalized, ct))
            throw Invalid("recipient", "Recipient is on your suppression list.");
    }

    private async Task<string> OpportunityFactsAsync(Guid opportunityId, CancellationToken ct) =>
        await db.Opportunities.Where(o => o.Id == opportunityId && o.OwnerId == user.OwnerId)
            .Select(o => o.FactsJson).SingleAsync(ct);

    private static Guid? RecipientEvidence(string? recipient, string? factsJson)
    {
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(factsJson)) return null;
        var normalized = Domain.Outreach.Suppression.Normalize(recipient).Replace("mailto:", string.Empty, StringComparison.OrdinalIgnoreCase);
        var facts = Deserialize<List<FactRow>>(factsJson) ?? [];
        var match = facts.FirstOrDefault(f => !f.IsInference && !string.IsNullOrWhiteSpace(f.EvidenceId) &&
            f.Value.Replace("mailto:", string.Empty, StringComparison.OrdinalIgnoreCase).Contains(normalized, StringComparison.OrdinalIgnoreCase));
        return Guid.TryParse(match?.EvidenceId, out var id) ? id : null;
    }

    /// <summary>
    /// A LinkedIn message may be addressed to a person's name or to their LinkedIn profile link; a link is stored in its one normal
    /// form (https://www.linkedin.com/in/{slug}) so the desktop agent's outbox and the suppression list recognise it. Any other web
    /// address is refused. Other channels keep the recipient as typed.
    /// </summary>
    private static string? NormalizeRecipient(DraftChannel channel, string? recipient)
    {
        if (channel != DraftChannel.LinkedInMessage || string.IsNullOrWhiteSpace(recipient)) return recipient;
        var trimmed = recipient.Trim();
        var looksLikeLink = trimmed.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("://", StringComparison.Ordinal)
                            || trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeLink) return recipient;
        return Domain.Circle.LinkedInProfileUrl.NormalizeOrNull(trimmed)
               ?? throw Invalid("recipient", "Use a LinkedIn profile link like https://www.linkedin.com/in/name, or the person's name.");
    }

    private static bool ContainsUnresolvedPlaceholder(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var open = value.IndexOf('[');
        return open >= 0 && value.IndexOf(']', open + 1) > open + 1;
    }

    private static string ChannelName(DraftChannel channel) => channel switch
    {
        DraftChannel.CoverNote => "cover note",
        DraftChannel.LinkedInMessage => "LinkedIn message",
        DraftChannel.ContactForm => "contact form",
        _ => channel.ToString().ToLowerInvariant()
    };
    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static RequestValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
