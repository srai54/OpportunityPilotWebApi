using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Feedback;

namespace OpportunityPilot.Api.Hosting;

/// <summary>
/// Turns application exceptions into ProblemDetails. Unexpected errors never expose internals; they are logged and also counted in the
/// admin's error list (Settings → Feedback and errors) with the exception type and route only.
/// </summary>
public sealed partial class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger, IServiceScopeFactory scopes,
    TimeProvider clock) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        ProblemDetails problem = exception switch
        {
            RequestValidationException v => new ValidationProblemDetails(v.Errors) { Status = 400, Title = "Validation failed" },
            NotFoundException n => new ProblemDetails { Status = 404, Title = "Not found", Detail = n.Message },
            ConflictException c => new ProblemDetails { Status = 409, Title = "Conflict", Detail = c.Message },
            ForbiddenException f => new ProblemDetails { Status = 403, Title = "Forbidden", Detail = f.Message },
            UnauthorizedAccessException => new ProblemDetails { Status = 401, Title = "Unauthorized" },
            BadHttpRequestException b => new ProblemDetails { Status = b.StatusCode, Title = "Bad request" },
            _ => new ProblemDetails { Status = 500, Title = "Unexpected error", Detail = "The error was logged with the correlation id below." }
        };

        if (problem.Status == 500)
        {
            logger.LogError(exception, "Unhandled exception");
            await RecordAsync(context, exception);
        }

        context.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }

    /// <summary>Counts the 500 in its own scope (the request's context may be the thing that failed). Never throws.</summary>
    private async Task RecordAsync(HttpContext context, Exception exception)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            // Route shape only: ids and tokens in the path are replaced, so no personal data or link token is stored.
            var path = Ids().Replace(context.Request.Path.Value ?? "/", "{id}");
            await FeedbackService.RecordServerErrorAsync(db, exception.GetType().Name, context.Request.Method, path, context.TraceIdentifier,
                typeof(AppExceptionHandler).Assembly.GetName().Version?.ToString(), clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record the server error in the error list");
        }
    }

    /// <summary>Guids, numbers and long tokens in a path segment.</summary>
    [GeneratedRegex(@"(?<=/)([0-9a-fA-F-]{32,36}|\d+|[A-Za-z0-9_-]{24,})(?=/|$)")]
    private static partial Regex Ids();
}
