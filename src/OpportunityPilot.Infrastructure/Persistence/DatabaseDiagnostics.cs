using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Configuration;

namespace OpportunityPilot.Infrastructure.Persistence;

/// <summary>Reachability, pending migrations and (on Postgres) whether Supabase's anon/authenticated roles can use schema app.</summary>
public sealed class DatabaseDiagnosticsProbe(AppDbContext db, IOptions<DatabaseOptions> options, SetupState setup) : IDatabaseDiagnostics
{
    public const string ExposureSql = """
        SELECT r.rolname AS "Value"
        FROM pg_roles r
        WHERE r.rolname IN ('anon', 'authenticated')
          AND has_schema_privilege(r.rolname, 'app', 'USAGE')
        """;

    public async Task<DatabaseDiagnostics> CheckAsync(CancellationToken ct)
    {
        if (!setup.DatabaseConfigured || !db.Database.IsRelational())
            return new("InMemory", false, true, null, new SchemaExposure(false, [], "Demo mode: data is in memory, nothing is exposed."));

        var provider = options.Value.Provider ?? "Unknown";
        bool reachable;
        try { reachable = await db.Database.CanConnectAsync(ct); }
        catch (Exception) { reachable = false; }
        if (!reachable) return new(provider, true, false, null, new SchemaExposure(false, [], "Database not reachable."));

        int? pending = null;
        try { pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count(); } catch (Exception) { /* reported as unknown */ }

        if (!db.Database.IsNpgsql())
            return new(provider, true, true, pending, new SchemaExposure(false, [], "Only Postgres (Supabase) exposes schemas over REST."));
        try
        {
            var exposed = await db.Database.SqlQueryRaw<string>(ExposureSql).ToListAsync(ct);
            return new(provider, true, true, pending, new SchemaExposure(true, exposed,
                exposed.Count == 0 ? "Not exposed: anon and authenticated cannot use schema app." : "EXPOSED — see docs/operations.md §5."));
        }
        catch (Exception)
        {
            return new(provider, true, true, pending, new SchemaExposure(false, [], "The exposure check could not run with this database user."));
        }
    }
}
