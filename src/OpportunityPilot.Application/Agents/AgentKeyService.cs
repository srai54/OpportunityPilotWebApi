using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Agents;

namespace OpportunityPilot.Application.Agents;

public sealed class AgentKeyService(IAppDbContext db, ICurrentUser user, TimeProvider clock, Auth.SecurityAudit audit)
{
    public const string KeyPrefix = "opk_";
    public const int PrefixLength = 12;
    public const int MaxNameLength = 100;
    public const int MaxActiveKeysPerOwner = 10;

    // "opk_" + 43 base64url characters; anything much longer is not one of ours and is rejected before hashing.
    private const int MaxPresentedKeyLength = 100;

    private IQueryable<AgentKey> Owned => db.AgentKeys.Where(k => k.OwnerId == user.OwnerId);

    public async Task<IReadOnlyList<AgentKeyDto>> ListAsync(CancellationToken ct)
    {
        var keys = await Owned.Where(k => k.RevokedAt == null).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return keys.Select(k => new AgentKeyDto(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, ScopeNames(k.Scopes))).ToList();
    }

    public async Task<CreatedAgentKeyDto> CreateAsync(CreateAgentKeyRequest request, CancellationToken ct)
    {
        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["name"] = ["Name is required."] });
        if (name.Length > MaxNameLength)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["name"] = [$"Name must be at most {MaxNameLength} characters."] });
        if (await Owned.CountAsync(k => k.RevokedAt == null, ct) >= MaxActiveKeysPerOwner)
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["name"] = [$"You already have {MaxActiveKeysPerOwner} active agent keys. Revoke one you no longer use first."]
            });

        var scopes = ParseScopes(request!.Scopes);
        var key = GenerateKey();
        var entity = new AgentKey(user.OwnerId, name, HashKey(key), PrefixOf(key), clock.GetUtcNow().UtcDateTime, scopes);
        db.AgentKeys.Add(entity);
        audit.Record(user.OwnerId, Domain.Auth.SecurityEventType.AgentKeyCreated, $"Key {entity.Prefix}… \"{entity.Name}\" with scopes {string.Join(", ", ScopeNames(scopes))}");
        await db.SaveChangesAsync(ct);
        return new CreatedAgentKeyDto(entity.Id, entity.Name, entity.Prefix, entity.CreatedAt, entity.LastUsedAt, key, ScopeNames(scopes));
    }

    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        var key = await Owned.FirstOrDefaultAsync(k => k.Id == id, ct)
            ?? throw new NotFoundException("Agent key not found.");
        if (key.RevokedAt is null) audit.Record(user.OwnerId, Domain.Auth.SecurityEventType.AgentKeyRevoked, $"Key {key.Prefix}… \"{key.Name}\"");
        key.Revoke(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolves a presented key to its owner, or null when it is unknown or revoked.
    /// Deliberately NOT owner-scoped: this is how the owner of an agent request is determined in the first place,
    /// so it must never be called with, or return data to, anyone but the authentication handler.
    /// </summary>
    public async Task<Guid?> ValidateAsync(string? presentedKey, CancellationToken ct) =>
        (await IdentifyAsync(presentedKey, ct))?.OwnerId;

    /// <summary>As <see cref="ValidateAsync"/>, with the key's scopes.</summary>
    public async Task<AgentKeyIdentity?> IdentifyAsync(string? presentedKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length > MaxPresentedKeyLength
            || !presentedKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return null;

        var hash = HashKey(presentedKey);
        var key = await db.AgentKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (key is null) return null;

        key.MarkUsed(clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return new AgentKeyIdentity(key.OwnerId, key.Scopes);
    }

    public static IReadOnlyList<string> ScopeNames(AgentKeyScope scopes) =>
        new[] { AgentKeyScope.Research, AgentKeyScope.Shortlist, AgentKeyScope.Applications, AgentKeyScope.People }.Where(s => scopes.HasFlag(s)).Select(s => s.ToString()).ToList();

    private static AgentKeyScope ParseScopes(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return AgentKeyScope.All;
        var result = AgentKeyScope.None;
        foreach (var name in names)
        {
            if (!Enum.TryParse<AgentKeyScope>(name, ignoreCase: true, out var s) || s is AgentKeyScope.None or AgentKeyScope.All)
                throw new RequestValidationException(new Dictionary<string, string[]> { ["scopes"] = [$"Unknown scope \"{name}\". Use Research, Shortlist, Applications or People."] });
            result |= s;
        }
        return result;
    }

    /// <summary>"opk_" followed by 32 random bytes as unpadded base64url.</summary>
    public static string GenerateKey() => KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex SHA-256. The key is high-entropy random, so a fast unsalted hash is sufficient.</summary>
    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string PrefixOf(string key) => key[..Math.Min(PrefixLength, key.Length)];
}
