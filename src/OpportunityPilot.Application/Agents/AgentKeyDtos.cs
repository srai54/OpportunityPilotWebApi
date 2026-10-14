using OpportunityPilot.Domain.Agents;

namespace OpportunityPilot.Application.Agents;

/// <summary>Never carries the key itself; <see cref="Prefix"/> is enough to tell keys apart.</summary>
public sealed record AgentKeyDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, IReadOnlyList<string>? Scopes = null);

/// <summary>Scopes default to all four (Research, Shortlist, Applications, People).</summary>
public sealed record CreateAgentKeyRequest(string Name, IReadOnlyList<string>? Scopes = null);

/// <summary>Returned once, at creation. The plaintext <see cref="Key"/> cannot be retrieved again.</summary>
public sealed record CreatedAgentKeyDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, string Key, IReadOnlyList<string>? Scopes = null);

/// <summary>The owner and what the presented key may do.</summary>
public sealed record AgentKeyIdentity(Guid OwnerId, AgentKeyScope Scopes);
