using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Access;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Feedback;

namespace OpportunityPilot.Application.Feedback;

public sealed record FeedbackRequest(string? Kind, int? Rating, string? Message, string? Page, string? AppVersion);
public sealed record FeedbackDto(Guid Id, string Kind, int? Rating, string Message, string? Page, string? AppVersion, string? Email, bool Guest,
    string Status, DateTime CreatedAt, DateTime? HandledAt);
public sealed record FeedbackStatusRequest(string? Status);
public sealed record ClientErrorRequest(string? Message, string? Source, string? Page, string? Stack, string? UserAgent, string? AppVersion, string? CorrelationId);
public sealed record ClientErrorDto(Guid Id, string Message, string? Source, string? Page, string? Stack, string? AppVersion, string? CorrelationId,
    int Count, DateTime FirstSeenAt, DateTime LastSeenAt);
public sealed record FeedbackSummaryDto(int New, int Last7Days, double? AverageRating, IReadOnlyDictionary<string, int> ByKind, int ErrorsLast24h, int ErrorKinds);

/// <summary>
/// Feedback and client errors (2026-10-26): people send feedback from any page; the web app reports browser errors; the admin sees both in
/// Settings with a summary. Errors are also written to the server log with their correlation id, so one id finds both sides.
/// </summary>
public sealed class FeedbackService(IAppDbContext db, ICurrentUser user, ICallerInfo caller, AccessService access, TimeProvider clock, ILogger<FeedbackService> log)
{
    private static RequestValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    private static FeedbackDto Dto(UserFeedback f) => new(f.Id, f.Kind.ToString(), f.Rating, f.Message, f.Page, f.AppVersion, f.Email, f.Guest,
        f.Status.ToString(), f.CreatedAt, f.HandledAt);

    private void AdminOnly()
    {
        if (!access.IsAdmin()) throw new ForbiddenException("Only the admin can see feedback.");
    }

    public async Task<FeedbackDto> SendAsync(FeedbackRequest request, CancellationToken ct)
    {
        if (request is null || !Enum.TryParse<FeedbackKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind)) throw Invalid("kind", "Choose Bug, Idea, Praise or Other.");
        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length < 3) throw Invalid("message", "Tell us a little more (a few words at least).");
        if (message.Length > UserFeedback.MaxMessageLength) throw Invalid("message", $"Keep it under {UserFeedback.MaxMessageLength} characters.");
        if (request.Rating is < 1 or > 5) throw Invalid("rating", "Rating 1 to 5.");
        var f = new UserFeedback(user.OwnerId, caller.IsGuest, caller.Email, kind, request.Rating, message, request.Page?.Trim(), request.AppVersion?.Trim(), clock.GetUtcNow().UtcDateTime);
        db.UserFeedback.Add(f);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Feedback {FeedbackId} received: {Kind}, rating {Rating}, page {Page}", f.Id, f.Kind, f.Rating, f.Page);
        return Dto(f);
    }

    /// <summary>Records a browser error. The same message from the same page within an hour is counted on the first row instead of stored again.</summary>
    public async Task ReportErrorAsync(ClientErrorRequest request, Guid? ownerId, CancellationToken ct)
    {
        var message = request?.Message?.Trim();
        if (string.IsNullOrEmpty(message)) throw Invalid("message", "The error message is required.");
        var page = request!.Page?.Trim();
        await RecordAsync(db, new ClientError(ownerId, message, request.Source, page, request.Stack, request.UserAgent, request.AppVersion, request.CorrelationId,
            clock.GetUtcNow().UtcDateTime), ct);
        log.LogWarning("Client error on {Page}: {Message} (correlation {CorrelationId}, version {AppVersion})", page, message, request.CorrelationId, request.AppVersion);
    }

    /// <summary>
    /// An unexpected server error (a 500), recorded next to the browser errors so the admin sees both in one list. Only the exception type,
    /// the method and the route are kept (never the exception text, which can hold personal data); the correlation id finds the full log.
    /// </summary>
    public static Task RecordServerErrorAsync(IAppDbContext db, string exceptionType, string method, string path, string correlationId, string? appVersion,
        DateTime utcNow, CancellationToken ct) =>
        RecordAsync(db, new ClientError(null, $"{exceptionType} on {method}", "server", path, null, null, appVersion, correlationId, utcNow), ct);

    /// <summary>Stores the error, or counts it on the row of the same message and page seen within the last hour.</summary>
    private static async Task RecordAsync(IAppDbContext db, ClientError error, CancellationToken ct)
    {
        var since = error.LastSeenAt.AddHours(-1);
        var recent = await db.ClientErrors.FirstOrDefaultAsync(e => e.Message == error.Message && e.Page == error.Page && e.LastSeenAt > since, ct);
        if (recent is not null) recent.SeenAgain(error.LastSeenAt);
        else db.ClientErrors.Add(error);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FeedbackDto>> ListAsync(CancellationToken ct)
    {
        AdminOnly();
        return (await db.UserFeedback.AsNoTracking().OrderByDescending(f => f.CreatedAt).Take(200).ToListAsync(ct)).Select(Dto).ToList();
    }

    public async Task<FeedbackDto> SetStatusAsync(Guid id, FeedbackStatusRequest request, CancellationToken ct)
    {
        AdminOnly();
        if (request is null || !Enum.TryParse<FeedbackStatus>(request.Status, true, out var status) || !Enum.IsDefined(status)) throw Invalid("status", "Choose New, Seen or Done.");
        var f = await db.UserFeedback.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Feedback not found.");
        f.SetStatus(status, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return Dto(f);
    }

    public async Task<IReadOnlyList<ClientErrorDto>> ErrorsAsync(CancellationToken ct)
    {
        AdminOnly();
        return (await db.ClientErrors.AsNoTracking().OrderByDescending(e => e.LastSeenAt).Take(100).ToListAsync(ct))
            .Select(e => new ClientErrorDto(e.Id, e.Message, e.Source, e.Page, e.Stack, e.AppVersion, e.CorrelationId, e.Count, e.FirstSeenAt, e.LastSeenAt)).ToList();
    }

    public async Task<FeedbackSummaryDto> SummaryAsync(CancellationToken ct)
    {
        AdminOnly();
        var now = clock.GetUtcNow().UtcDateTime;
        var all = await db.UserFeedback.AsNoTracking().Select(f => new { f.Kind, f.Rating, f.Status, f.CreatedAt }).ToListAsync(ct);
        var ratings = all.Where(f => f.Rating is not null).Select(f => f.Rating!.Value).ToList();
        var errors = await db.ClientErrors.AsNoTracking().Where(e => e.LastSeenAt > now.AddHours(-24)).Select(e => new { e.Count }).ToListAsync(ct);
        return new FeedbackSummaryDto(all.Count(f => f.Status == FeedbackStatus.New), all.Count(f => f.CreatedAt > now.AddDays(-7)),
            ratings.Count == 0 ? null : Math.Round(ratings.Average(), 1),
            Enum.GetValues<FeedbackKind>().ToDictionary(k => k.ToString(), k => all.Count(f => f.Kind == k)),
            errors.Sum(e => e.Count), errors.Count);
    }
}
