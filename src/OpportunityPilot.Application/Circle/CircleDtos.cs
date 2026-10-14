using OpportunityPilot.Domain.Circle;
using OpportunityPilot.Domain.Common;

namespace OpportunityPilot.Application.Circle;

/// <param name="Title">The person's LinkedIn headline or job title as the search card showed it.</param>
/// <param name="Degree">1, 2 or 3 (3rd+); null when unknown.</param>
public sealed record CirclePersonDto(
    Guid Id, string Name, string? Title, string ProfileUrl, CircleRelation Relation, int? Degree, CircleSource Source, DateTime FoundAt);

/// <param name="Status">NotRequested (never asked), Waiting (asked, the agent has not delivered since) or Ready.</param>
public sealed record CircleDto(Guid OpportunityId, DateTime? RequestedAt, DateTime? MappedAt, string Status, IReadOnlyList<CirclePersonDto> People);

public sealed record AddCirclePersonRequest(string Name, string ProfileUrl, CircleRelation Relation, string? Title = null, int? Degree = null);

/// <summary>A lead the person asked the desktop agent to map, with what the agent searches LinkedIn for.</summary>
public sealed record AgentCircleRequestDto(Guid OpportunityId, OpportunityMode Mode, string Organization, string Title, string? Location, DateTime RequestedAt);

public sealed record AgentCirclePerson(string Name, string? Title, string ProfileUrl, CircleRelation Relation, int? Degree);

public sealed record AgentCircleRequest(Guid OpportunityId, IReadOnlyList<AgentCirclePerson>? People);

/// <param name="Accepted">Distinct people in the request (the same profile twice counts once).</param>
/// <param name="Total">People now in this lead's circle.</param>
public sealed record AgentCircleResult(int Accepted, int Added, int Updated, int Total);
