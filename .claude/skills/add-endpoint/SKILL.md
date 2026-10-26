---
name: add-endpoint
description: How to add or change an OpportunityPilot API endpoint end to end — controller, service, validation, auth policy, rate limit, owner scoping, tests and the docs that must change with it. Read before adding a route, changing a request/response shape or exposing an anonymous endpoint.
---

# Add or change an endpoint

The layering is Controller → service (`Application/<Feature>/`) → `IAppDbContext` or a repository. Controllers stay thin: one line that
calls the service and returns `Ok(...)`. Errors are exceptions the service throws; `Hosting/AppExceptionHandler.cs` maps them.

## Decision table

| Need | Do |
|---|---|
| Bad input | throw `RequestValidationException` with field → messages (400, the web app shows the field reasons) |
| Not found, or owned by someone else | `NotFoundException` (404; never reveal that another owner's record exists) |
| Stale version / state conflict / provider unavailable | `ConflictException` (409) |
| Signed in but not allowed (admin-only) | `ForbiddenException` (403) |
| Account only, no guests | `[Authorize(Policy = Auth.Policies.NotGuest)]` |
| No sign-in (candidate link, browser error report) | `[AllowAnonymous]` **plus** a rate limit **plus** an unguessable token or a payload that is harmless to store |
| Spends a paid or shared quota (AI, job boards, mail) | its own policy in `Hosting/RateLimits.cs` (name, permits, window) and `[EnableRateLimiting(...)]` on the action |
| Reads or writes owner data | filter every query by `user.OwnerId` (or the link's owner); never trust an id from the client alone |
| Stores a secret (API key, token) | encrypt with `TokenProtector`, keep only a hint, never return or log it (see `ClaudeKeyService`) |

## Steps

1. Request and response records next to the service (`sealed record XRequest(...)`, `XDto(...)`); validate in the service.
2. Service method with owner scoping; register the service in `Application/DependencyInjection.cs`.
3. Controller action with the verb, route, auth and rate-limit attributes; a `/// <summary>` when the behaviour is not obvious.
4. Unit tests in `tests/OpportunityPilot.UnitTests` (InMemory DB): happy path, validation, another owner's id → 404, and the limits.
5. Docs in the same commit:
   - `python tools/endpoints.py` (regenerates `docs/api-endpoints.md`; CI fails if you forget);
   - `docs/api-contracts.md`: the body and response shape and the error codes;
   - a new table → the `migrations` skill and `docs/db-schema.md`; personal data → `docs/PRIVACY.md`.
6. `/verify`, then call the endpoint once locally (`curl -H X-Dev-User:dev@example.com …`) and report the real status codes.
