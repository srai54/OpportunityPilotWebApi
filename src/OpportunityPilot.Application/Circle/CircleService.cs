using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Circle;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Circle;

/// <summary>
/// The "JD circle": the people around a job or company lead. The person asks for a mapping; the desktop agent searches LinkedIn
/// people in the person's own logged-in browser and delivers what the result cards showed (name, headline, profile link, degree).
/// The person can also add or remove people by hand. Everything is owner-scoped; another owner's lead answers 404.
/// </summary>
public sealed class CircleService(IAppDbContext db, ICurrentUser user, TimeProvider clock)
{
    public const int MaxPeoplePerDelivery = 50;
    public const int MaxPeoplePerOpportunity = 200;
    public const int MaxRequestsForAgent = 20;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<CircleDto> GetAsync(Guid opportunityId, CancellationToken ct) =>
        await ToDtoAsync(await FindOpportunityAsync(opportunityId, ct), ct);

    /// <summary>Queues a mapping for the agent. Idempotent while one is waiting.</summary>
    public async Task<CircleDto> RequestAsync(Guid opportunityId, CancellationToken ct)
    {
        var opportunity = await FindOpportunityAsync(opportunityId, ct);
        if (string.IsNullOrWhiteSpace(opportunity.Organization))
            throw Invalid("organization", "This lead has no company name, so there is nobody to look up. Add the company first.");
        opportunity.RequestCircle(Now);
        await SaveAsync(ct);
        return await ToDtoAsync(opportunity, ct);
    }

    public async Task<CirclePersonDto> AddAsync(Guid opportunityId, AddCirclePersonRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        var opportunity = await FindOpportunityAsync(opportunityId, ct);
        var errors = new Dictionary<string, string[]>();
        var url = ValidatePerson(errors, string.Empty, request.Name, request.Title, request.ProfileUrl, request.Relation, request.Degree);
        if (errors.Count > 0) throw new RequestValidationException(errors);

        var existing = await PeopleOf(opportunity.Id).ToListAsync(ct);
        if (existing.Any(p => Same(p.ProfileUrl, url!)))
            throw new ConflictException("This person is already in the circle.");
        if (existing.Count >= MaxPeoplePerOpportunity)
            throw Invalid("people", $"A circle holds at most {MaxPeoplePerOpportunity} people. Remove someone first.");
        var person = new CirclePerson(user.OwnerId, opportunity.Id, request.Name, request.Title, url!, request.Relation, request.Degree,
            CircleSource.Manual, Now);
        db.CirclePeople.Add(person);
        await SaveAsync(ct);
        return ToDto(person);
    }

    public async Task DeleteAsync(Guid opportunityId, Guid personId, CancellationToken ct)
    {
        var person = await db.CirclePeople.FirstOrDefaultAsync(p => p.Id == personId && p.OpportunityId == opportunityId && p.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Person not found.");
        db.CirclePeople.Remove(person);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Leads waiting for a mapping, oldest request first.</summary>
    public async Task<IReadOnlyList<AgentCircleRequestDto>> PendingForAgentAsync(CancellationToken ct)
    {
        var rows = await db.Opportunities.AsNoTracking()
            .Where(o => o.OwnerId == user.OwnerId && o.CircleRequestedAt != null && o.Organization != ""
                        && (o.CircleMappedAt == null || o.CircleMappedAt < o.CircleRequestedAt))
            .OrderBy(o => o.CircleRequestedAt).ThenBy(o => o.Id)
            .Take(MaxRequestsForAgent)
            .ToListAsync(ct);
        return rows.Select(o => new AgentCircleRequestDto(o.Id, o.Mode, o.Organization, o.Title, o.Location, o.CircleRequestedAt!.Value)).ToList();
    }

    /// <summary>
    /// Upserts what the agent found (by profile link) and marks the lead mapped, even when nobody was found. A person the user added
    /// by hand keeps the relation the user chose.
    /// </summary>
    public async Task<AgentCircleResult> DeliverAsync(AgentCircleRequest request, CancellationToken ct)
    {
        if (request is null) throw Invalid("body", "Request body is required.");
        var people = request.People ?? [];
        var errors = new Dictionary<string, string[]>();
        if (people.Count > MaxPeoplePerDelivery) errors["people"] = [$"At most {MaxPeoplePerDelivery} people can be sent at once."];
        var latest = new Dictionary<string, (AgentCirclePerson Person, string Url)>(StringComparer.OrdinalIgnoreCase);
        if (errors.Count == 0)
            for (var i = 0; i < people.Count; i++)
            {
                var p = people[i];
                if (p is null) { errors[$"people[{i}]"] = ["Person is required."]; continue; }
                var url = ValidatePerson(errors, $"people[{i}].", p.Name, p.Title, p.ProfileUrl, p.Relation, p.Degree);
                if (url is not null) latest[url] = (p, url);   // the same person twice: the last one wins
            }
        if (errors.Count > 0) throw new RequestValidationException(errors);

        var opportunity = await FindOpportunityAsync(request.OpportunityId, ct);
        var existing = (await PeopleOf(opportunity.Id).ToListAsync(ct))
            .GroupBy(p => p.ProfileUrl, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var newcomers = latest.Keys.Count(k => !existing.ContainsKey(k));
        if (existing.Count + newcomers > MaxPeoplePerOpportunity)
            throw Invalid("people", $"A circle holds at most {MaxPeoplePerOpportunity} people; this one has {existing.Count}.");

        var now = Now;
        int added = 0, updated = 0;
        foreach (var (url, (p, _)) in latest)
        {
            if (existing.TryGetValue(url, out var row))
            {
                row.Refresh(p.Name, p.Title, p.Relation, p.Degree, now);
                updated++;
            }
            else
            {
                db.CirclePeople.Add(new CirclePerson(user.OwnerId, opportunity.Id, p.Name, p.Title, url, p.Relation, p.Degree, CircleSource.LinkedInAgent, now));
                added++;
            }
        }
        opportunity.MarkCircleMapped(now);
        db.Activities.Add(new Activity(user.OwnerId, opportunity.Id, "Note", now,
            latest.Count == 0
                ? "Mapped the people around this lead on LinkedIn (desktop agent, your browser): nobody found."
                : $"Mapped the people around this lead on LinkedIn (desktop agent, your browser): {latest.Count} found, {added} new."));
        await SaveAsync(ct);
        return new AgentCircleResult(latest.Count, added, updated, existing.Count + added);
    }

    private IQueryable<CirclePerson> PeopleOf(Guid opportunityId) =>
        db.CirclePeople.Where(p => p.OwnerId == user.OwnerId && p.OpportunityId == opportunityId);

    private async Task<Opportunity> FindOpportunityAsync(Guid id, CancellationToken ct) =>
        await db.Opportunities.FirstOrDefaultAsync(o => o.Id == id && o.OwnerId == user.OwnerId, ct)
        ?? throw new NotFoundException("Opportunity not found.");

    private async Task<CircleDto> ToDtoAsync(Opportunity o, CancellationToken ct)
    {
        var people = await PeopleOf(o.Id).AsNoTracking().ToListAsync(ct);
        var ordered = people.OrderBy(p => p.Relation).ThenBy(p => p.Degree ?? 9).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id);
        var status = o.CircleRequestedAt is null ? "NotRequested" : o.CircleWaiting ? "Waiting" : "Ready";
        return new CircleDto(o.Id, o.CircleRequestedAt, o.CircleMappedAt, status, ordered.Select(ToDto).ToList());
    }

    private static CirclePersonDto ToDto(CirclePerson p) =>
        new(p.Id, p.Name, p.Headline, p.ProfileUrl, p.Relation, p.Degree, p.Source, p.FoundAt);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds field errors under <paramref name="prefix"/>; returns the normalised profile link when it is valid.</summary>
    private static string? ValidatePerson(Dictionary<string, string[]> errors, string prefix, string? name, string? title, string? profileUrl,
        CircleRelation relation, int? degree)
    {
        if (string.IsNullOrWhiteSpace(name)) errors[$"{prefix}name"] = ["Name is required."];
        else if (name.Trim().Length > CirclePerson.MaxNameLength) errors[$"{prefix}name"] = [$"Name must be at most {CirclePerson.MaxNameLength} characters."];
        if (title is not null && title.Trim().Length > CirclePerson.MaxHeadlineLength)
            errors[$"{prefix}title"] = [$"Title must be at most {CirclePerson.MaxHeadlineLength} characters."];
        if (!Enum.IsDefined(relation)) errors[$"{prefix}relation"] = ["Relation must be Recruiter, HiringManager, Team, Connection or Other."];
        if (degree is not null and not (1 or 2 or 3)) errors[$"{prefix}degree"] = ["Degree must be 1, 2, 3 or empty."];
        if (!LinkedInProfileUrl.TryNormalize(profileUrl, out var url))
        {
            errors[$"{prefix}profileUrl"] = ["Use a LinkedIn profile link like https://www.linkedin.com/in/name."];
            return null;
        }
        return url;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("This lead changed elsewhere. Reload and try again."); }
        catch (DbUpdateException) { throw new ConflictException("The same person was saved at the same time. Send it again."); }
    }

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
