using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Interfaces;

namespace TaskManager.Infrastructure.Persistence.Seeding;

public sealed record BootstrapOptions
{
    /// <summary>Create the database if missing and apply all pending EF Core migrations.</summary>
    public bool ApplyMigrations { get; init; } = true;

    /// <summary>Seed baseline data (roles / local Admin account, and a demo user in Development).</summary>
    public bool SeedBaseline { get; init; }

    /// <summary>Seed full demo data. Ignored (refused) when the environment is Production.</summary>
    public bool SeedDemoData { get; init; }

    /// <summary>Report the migration state and exit without changing anything.</summary>
    public bool StatusOnly { get; init; }
}

public sealed record BootstrapResult
{
    public required string Database { get; init; }
    public required string Provider { get; init; }
    public bool DatabaseExistedBefore { get; init; }
    public IReadOnlyList<string> AppliedMigrations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PendingBefore { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MigrationsJustApplied { get; init; } = Array.Empty<string>();
    public bool BaselineSeeded { get; init; }
    public bool DemoSeeded { get; init; }
    public bool DemoRefusedInProduction { get; init; }
    public DemoSeedSummary? DemoSummary { get; init; }
}

/// <summary>
/// One-shot database bootstrap used by the API on startup and by TaskManager.DbRunner:
/// create the database if it does not exist, apply pending migrations with
/// <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> (never EnsureCreated,
/// never drop/reset), then optionally seed baseline and/or demo data. Idempotent.
/// </summary>
public static class DatabaseBootstrapper
{
    public static async Task<BootstrapResult> RunAsync(
        IServiceProvider services, IHostEnvironment env, BootstrapOptions options, ILogger logger,
        CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskManagerDbContext>();

        var dbName = db.Database.GetDbConnection().Database;
        var provider = db.Database.ProviderName ?? "unknown";

        bool exists;
        try
        {
            exists = await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            exists = false;
        }

        var applied = new List<string>();
        List<string> pending;
        if (exists)
        {
            applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        }
        else
        {
            // No DB yet — every migration in the assembly is pending. GetMigrations() needs no connection.
            pending = db.Database.GetMigrations().ToList();
        }

        logger.LogInformation(
            "Database '{Database}' ({Provider}) — {State}. Applied: {Applied}, pending: {Pending}.",
            dbName, ShortProvider(provider),
            exists ? "reachable" : "does not exist yet",
            applied.Count, pending.Count);
        foreach (var m in pending)
        {
            logger.LogInformation("  pending migration: {Migration}", m);
        }

        var result = new BootstrapResult
        {
            Database = dbName,
            Provider = ShortProvider(provider),
            DatabaseExistedBefore = exists,
            AppliedMigrations = applied,
            PendingBefore = pending,
        };

        if (options.StatusOnly)
        {
            logger.LogInformation("Status-only run — no changes made.");
            return result;
        }

        var justApplied = Array.Empty<string>() as IReadOnlyList<string>;
        if (options.ApplyMigrations)
        {
            if (pending.Count == 0 && exists)
            {
                logger.LogInformation("Database schema is already up to date.");
            }
            else
            {
                logger.LogInformation("Applying {Count} migration(s)…", pending.Count);
                await db.Database.MigrateAsync(ct); // creates the DB if missing, then applies migrations
                justApplied = pending;
                logger.LogInformation(
                    exists ? "Migrations applied." : "Database created and migrations applied.");
            }
        }

        var baselineSeeded = false;
        if (options.SeedBaseline)
        {
            await DbSeeder.SeedAsync(services, env);
            baselineSeeded = true;
            logger.LogInformation("Baseline data seeded (roles / Admin account).");
        }

        var demoSeeded = false;
        var demoRefused = false;
        DemoSeedSummary? demoSummary = null;
        if (options.SeedDemoData)
        {
            if (env.IsProduction())
            {
                demoRefused = true;
                logger.LogWarning("Demo-data seeding is REFUSED in Production. Nothing was written.");
            }
            else
            {
                var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
                demoSummary = await DemoDataSeeder.SeedAsync(
                    db, hasher, logger, DateOnly.FromDateTime(DateTime.UtcNow), ct);
                demoSeeded = true;
            }
        }

        return result with
        {
            MigrationsJustApplied = justApplied,
            BaselineSeeded = baselineSeeded,
            DemoSeeded = demoSeeded,
            DemoRefusedInProduction = demoRefused,
            DemoSummary = demoSummary,
        };
    }

    private static string ShortProvider(string providerName) => providerName switch
    {
        var p when p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) => "PostgreSQL (Npgsql)",
        var p when p.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) => "SQL Server",
        var p when p.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) => "SQLite",
        _ => providerName,
    };
}
