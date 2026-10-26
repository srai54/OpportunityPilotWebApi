using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Mail;
using OpportunityPilot.Domain.Ai;

namespace OpportunityPilot.Application.Ai;

public sealed record ClaudeKeyStatusDto(bool Connected, string? KeyHint, string Model, IReadOnlyList<string> Models, bool ShareWithCandidates,
    DateTime? CheckedAt, bool CanStore);
public sealed record ConnectClaudeRequest(string? ApiKey, string? Model);
public sealed record ClaudeSettingsRequest(string? Model, bool? ShareWithCandidates);

/// <summary>What a test call with a key said. Only <see cref="Ok"/> is stored.</summary>
public enum ClaudeKeyCheck { Ok, Rejected, NoPermission, RateLimited, Unreachable }

/// <summary>Claude through the Anthropic API with a person's own key. The key is passed per call and never kept by the client.</summary>
public interface IClaudeClient
{
    /// <summary>A free call (the model list) that proves the key works.</summary>
    Task<ClaudeKeyCheck> CheckKeyAsync(string apiKey, CancellationToken ct);
    Task<LlmResult> GenerateJsonAsync(string apiKey, string model, string operation, string system, string input, int maxTokens, CancellationToken ct);
}

public sealed record UserClaudeKey(Guid OwnerId, string ApiKey, string Model);

/// <summary>The signed-in person's Claude key, or null (no key, no signed-in person, or the key can no longer be decrypted).</summary>
public interface IUserClaudeKey
{
    Task<UserClaudeKey?> CurrentAsync(CancellationToken ct);
    /// <summary>The key of a progress link's owner when they let candidates use it.</summary>
    Task<UserClaudeKey?> SharedByAsync(Guid ownerId, CancellationToken ct);
}

/// <summary>
/// Bring your own Claude key (2026-10-26). A person pastes an Anthropic API key in Settings; it is tested with a free call, stored encrypted
/// (AES-256-GCM, <see cref="TokenProtector"/>) and from then on every AI action of theirs runs on Claude. Only the last four characters are
/// ever shown. A Claude.ai or Claude Code subscription cannot be used this way: the Anthropic API needs its own key (console.anthropic.com).
/// </summary>
public sealed class ClaudeKeyService(IAppDbContext db, ICurrentUser user, TokenProtector protector, IClaudeClient claude, TimeProvider clock,
    ILogger<ClaudeKeyService> log) : IUserClaudeKey
{
    public const int MinKeyLength = 20, MaxKeyLength = 300;
    private bool _loaded;
    private UserClaudeKey? _current;

    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private ClaudeKeyStatusDto Dto(AiCredential? c) => new(c is not null, c is null ? null : "…" + c.KeyHint, c?.Model ?? AiCredential.DefaultModel,
        AiCredential.Models, c?.ShareWithCandidates ?? false, c?.CheckedAt, protector.Ready);

    public async Task<ClaudeKeyStatusDto> StatusAsync(CancellationToken ct) =>
        Dto(await db.AiCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId, ct));

    public async Task<ClaudeKeyStatusDto> ConnectAsync(ConnectClaudeRequest request, CancellationToken ct)
    {
        var key = request?.ApiKey?.Trim();
        if (string.IsNullOrEmpty(key) || !key.StartsWith("sk-ant-", StringComparison.Ordinal) || key.Length is < MinKeyLength or > MaxKeyLength || key.Any(char.IsWhiteSpace))
            throw Invalid("apiKey", "Paste an Anthropic API key (it starts with sk-ant-). Create one at console.anthropic.com → API keys.");
        if (key.StartsWith("sk-ant-admin", StringComparison.Ordinal))
            throw Invalid("apiKey", "That is an Admin key. Paste a normal API key instead; admin keys can manage your whole organisation.");
        if (request!.Model is { } m && !string.IsNullOrWhiteSpace(m) && !AiCredential.Models.Contains(m.Trim())) throw Invalid("model", "Choose one of the listed models.");
        if (!protector.Ready) throw new ConflictException("This server cannot store keys yet (no encryption key is set). Ask the admin to set Mail__TokenKey.");

        var check = await claude.CheckKeyAsync(key, ct);
        log.LogInformation("Claude key check for {OwnerId}: {Check}", user.OwnerId, check);
        switch (check)
        {
            case ClaudeKeyCheck.Rejected: throw Invalid("apiKey", "Anthropic did not accept this key. Check it was copied in full and has not been revoked.");
            case ClaudeKeyCheck.NoPermission: throw Invalid("apiKey", "This key may not use the API (permission denied). Ask your Anthropic organisation admin.");
            case ClaudeKeyCheck.RateLimited: throw new ConflictException("Anthropic is rate limiting this key right now. Try again in a minute.");
            case ClaudeKeyCheck.Unreachable: throw new ConflictException("Could not reach Anthropic to check the key. Try again in a minute.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var encrypted = protector.Protect(key);
        var hint = key[^4..];
        var row = await db.AiCredentials.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId, ct);
        if (row is null) db.AiCredentials.Add(row = new AiCredential(user.OwnerId, encrypted, hint, request.Model, now));
        else { row.Replace(encrypted, hint, now); if (!string.IsNullOrWhiteSpace(request.Model)) row.SetModel(request.Model); }
        await db.SaveChangesAsync(ct);
        _loaded = false;
        return Dto(row);
    }

    public async Task<ClaudeKeyStatusDto> UpdateAsync(ClaudeSettingsRequest request, CancellationToken ct)
    {
        var row = await db.AiCredentials.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId, ct) ?? throw new NotFoundException("Connect a Claude key first.");
        if (request?.Model is { } m && !string.IsNullOrWhiteSpace(m))
        {
            try { row.SetModel(m); } catch (ArgumentOutOfRangeException) { throw Invalid("model", "Choose one of the listed models."); }
        }
        if (request?.ShareWithCandidates is { } share) row.SetSharing(share);
        await db.SaveChangesAsync(ct);
        _loaded = false;
        return Dto(row);
    }

    public async Task RemoveAsync(CancellationToken ct)
    {
        var row = await db.AiCredentials.FirstOrDefaultAsync(c => c.OwnerId == user.OwnerId, ct);
        if (row is null) return;
        db.AiCredentials.Remove(row);
        await db.SaveChangesAsync(ct);
        _loaded = false;
        _current = null;
    }

    public async Task<UserClaudeKey?> CurrentAsync(CancellationToken ct)
    {
        if (_loaded) return _current;
        Guid owner;
        try { owner = user.OwnerId; } catch (UnauthorizedAccessException) { return null; }
        _current = await OpenAsync(owner, requireSharing: false, ct);
        _loaded = true;
        return _current;
    }

    public Task<UserClaudeKey?> SharedByAsync(Guid ownerId, CancellationToken ct) => OpenAsync(ownerId, requireSharing: true, ct);

    private async Task<UserClaudeKey?> OpenAsync(Guid owner, bool requireSharing, CancellationToken ct)
    {
        var row = await db.AiCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == owner, ct);
        if (row is null || (requireSharing && !row.ShareWithCandidates)) return null;
        var key = protector.Unprotect(row.EncryptedKey);
        if (key is null) log.LogWarning("Stored Claude key for {OwnerId} could not be decrypted (server key changed?)", owner);
        return key is null ? null : new UserClaudeKey(owner, key, row.Model);
    }
}
