using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Integrations;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Integrations;
using OpportunityPilot.Domain.Opportunities;

namespace OpportunityPilot.Application.Opportunities;

/// <param name="Source">Hunter or Apollo.</param>
/// <param name="Titles">Who to look for; defaults to people who hire and people who buy. Apollo filters by them; Hunter lists them first.</param>
/// <param name="Website">The company website (or bare domain). Needed for a job lead, whose link is the job board, not the company.</param>
public sealed record FindPeopleRequest(string? Source, IReadOnlyList<string>? Titles, string? Website);

public sealed record RevealPersonRequest(string? Source, string? PersonId);

/// <param name="CreditsNote">What this costs on the person's own Hunter or Apollo plan.</param>
public sealed record FindPeopleResult(string Source, string Domain, IReadOnlyList<FoundPersonDto> People, string CreditsNote);

public sealed record RevealPersonResult(string Source, FoundPersonDto Person, string CreditsNote);

/// <summary>
/// Real recruiters, hiring managers and decision makers when the company publishes no address, looked up with the person's own Hunter or
/// Apollo key. Results are third-party data labelled with their source, never marked verified and never stored; the only thing written
/// is an activity line on the lead. A draft that later uses one of these addresses shows it as typed by the person (unverified).
/// </summary>
public sealed class PeopleFinder(IAppDbContext db, ICurrentUser user, TimeProvider clock, TokenProtector protector, IEnumerable<IPeopleSource> sources)
{
    public const string HunterCredits = "Uses one search from your Hunter plan when Hunter finds someone (Free plans get a small monthly allowance).";
    public const string ApolloSearchCredits = "Apollo's people search uses no credits. Emails are hidden: revealing one uses an Apollo credit.";

    public async Task<FindPeopleResult> FindAsync(Guid opportunityId, FindPeopleRequest? request, CancellationToken ct)
    {
        var kind = Kind(request?.Source);
        var titles = request?.Titles ?? [];
        if (titles.Count > PeopleSearch.MaxTitles || titles.Any(t => t?.Length > PeopleSearch.MaxTitleLength))
            throw Invalid("titles", $"At most {PeopleSearch.MaxTitles} titles of up to {PeopleSearch.MaxTitleLength} characters.");
        var opp = await OpportunityAsync(opportunityId, ct);

        var website = string.IsNullOrWhiteSpace(request?.Website) ? (opp.Mode == OpportunityMode.Job ? null : opp.Url) : request!.Website;
        if (string.IsNullOrWhiteSpace(website)) throw Invalid("website", "Add the company website first.");
        var domain = PeopleSearch.DomainOf(website) ?? throw Invalid("website", "Use the company's own website, for example https://acme.com.");

        var (row, key) = await ConnectionAsync(kind, ct);
        var people = await CallAsync(row, () => Source(kind).FindAsync(key, domain, PeopleSearch.Titles(titles), ct), ct);

        var name = IntegrationKinds.Name(kind);
        db.Activities.Add(new Activity(user.OwnerId, opp.Id, "Note", Now,
            $"Looked up {people.Count} {(people.Count == 1 ? "person" : "people")} at {domain} with {name} (third-party data, not verified)."));
        row.MarkUsed(Now);
        await db.SaveChangesAsync(ct);
        return new(name, domain, people, kind == IntegrationKind.Hunter ? HunterCredits : ApolloSearchCredits);
    }

    /// <summary>Apollo only: the work email of one person from an Apollo search. Uses the person's Apollo credits.</summary>
    public async Task<RevealPersonResult> RevealAsync(Guid opportunityId, RevealPersonRequest? request, CancellationToken ct)
    {
        var kind = Kind(request?.Source);
        if (kind != IntegrationKind.Apollo) throw Invalid("source", "Only Apollo hides emails; Hunter shows them in the search.");
        var personId = request?.PersonId?.Trim();
        if (!ApolloSource.IsPersonId(personId)) throw Invalid("personId", "Choose a person from an Apollo search.");
        var opp = await OpportunityAsync(opportunityId, ct);

        var (row, key) = await ConnectionAsync(kind, ct);
        var person = await CallAsync(row, () => Source(kind).RevealAsync(key, personId!, ct), ct)
            ?? throw new NotFoundException("Apollo has no person with that id.");

        db.Activities.Add(new Activity(user.OwnerId, opp.Id, "Note", Now,
            person.Email is null ? "Asked Apollo for a work email: none returned." : "Revealed a work email with Apollo (uses an Apollo credit; third-party data, not verified)."));
        row.MarkUsed(Now);
        await db.SaveChangesAsync(ct);
        return new("Apollo", person, ApolloSource.CreditsNote);
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private IPeopleSource Source(IntegrationKind kind) => sources.First(s => s.Kind == kind);

    private static IntegrationKind Kind(string? source) =>
        Enum.TryParse<IntegrationKind>(source?.Trim(), true, out var k) && IntegrationKinds.IsPeopleSource(k) ? k
            : throw Invalid("source", "Choose Hunter or Apollo.");

    private async Task<Opportunity> OpportunityAsync(Guid id, CancellationToken ct) =>
        await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.OwnerId == user.OwnerId, ct)
        ?? throw new NotFoundException("Opportunity not found.");

    private async Task<(IntegrationConnection Row, string Key)> ConnectionAsync(IntegrationKind kind, CancellationToken ct)
    {
        var name = IntegrationKinds.Name(kind);
        var row = await db.IntegrationConnections.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId && c.Kind == kind, ct)
            ?? throw new ConflictException($"Connect {name} in Connectors first.");
        var key = protector.Unprotect(row.EncryptedSecret) ?? throw new ConflictException($"The stored {name} key can no longer be read on this server. Connect {name} again.");
        return (row, key);
    }

    /// <summary>A refusal from the service (key refused, quota used up) is a 409 with its reason, and is kept as the connection's last error.</summary>
    private async Task<T> CallAsync<T>(IntegrationConnection row, Func<Task<T>> call, CancellationToken ct)
    {
        try { return await call(); }
        catch (ConnectorProblem p)
        {
            row.MarkFailed(p.Message);
            await db.SaveChangesAsync(ct);
            throw new ConflictException(p.Message);
        }
    }

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
