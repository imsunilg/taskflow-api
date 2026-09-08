using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskManager.Application;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Infrastructure;
using TaskManager.Infrastructure.Persistence.Seeding;

namespace TaskManager.DbRunner;

/// <summary>
/// One-shot database bootstrap / demo-data runner for TaskFlow.
///
///   dotnet run --project src/TaskManager.DbRunner -- --migrate       create DB (if missing) + apply migrations
///   dotnet run --project src/TaskManager.DbRunner -- --seed          the above + baseline (roles / Admin)
///   dotnet run --project src/TaskManager.DbRunner -- --seed-demo     the above + full demo data (never in Production)
///   dotnet run --project src/TaskManager.DbRunner -- --all           alias for --seed-demo
///   dotnet run --project src/TaskManager.DbRunner -- --status        report migration state only, change nothing
///
/// Uses EF Core migrations (Database.Migrate) — never EnsureCreated, never drop/reset/truncate.
/// Every step is idempotent and safe to run repeatedly.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var verbs = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.ToLowerInvariant()).ToHashSet();

        if (verbs.Count == 0 || verbs.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
        {
            PrintHelp();
            return verbs.Count == 0 ? 2 : 0;
        }

        var options = ResolveOptions(verbs, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            PrintHelp();
            return 2;
        }

        var environmentName =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development"; // a dev tool defaults to Development; set the env var for Production behaviour

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            EnvironmentName = environmentName,
            // The API's appsettings files are linked + copied next to the runner dll, so
            // resolve config from there regardless of the caller's working directory.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Optional --connection "<value>" overrides the configured connection string.
        var connOverride = ValueOf(args, "--connection");
        if (!string.IsNullOrWhiteSpace(connOverride))
        {
            builder.Configuration["ConnectionStrings:Postgres"] = connOverride;
        }

        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<ICurrentUserService, SystemCurrentUserService>();
        // The API supplies these; the runner never dispatches notifications or runs jobs,
        // but the container validates every registration on build.
        builder.Services.AddSingleton<INotificationDispatcher, NoOpNotificationDispatcher>();

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DbRunner");

        logger.LogInformation("TaskFlow DB runner — environment: {Environment}", environmentName);

        try
        {
            var result = await DatabaseBootstrapper.RunAsync(host.Services, builder.Environment, options, logger);
            PrintSummary(result);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database bootstrap failed: {Message}", ex.Message);
            return 1;
        }
    }

    private static BootstrapOptions ResolveOptions(HashSet<string> verbs, out string? error)
    {
        error = null;

        if (verbs.Contains("--status"))
        {
            return new BootstrapOptions { StatusOnly = true, ApplyMigrations = false, SeedBaseline = false };
        }

        var demo = verbs.Contains("--seed-demo") || verbs.Contains("--seed-demo-data")
                   || verbs.Contains("--demo") || verbs.Contains("--all");
        var seed = demo || verbs.Contains("--seed");
        var migrate = seed || verbs.Contains("--migrate");

        if (!migrate)
        {
            error = $"Unknown option(s): {string.Join(' ', verbs)}";
            return new BootstrapOptions();
        }

        return new BootstrapOptions
        {
            ApplyMigrations = true,
            SeedBaseline = seed,
            SeedDemoData = demo,
        };
    }

    private static string? ValueOf(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintSummary(BootstrapResult r)
    {
        Console.WriteLine();
        Console.WriteLine("──────────── TaskFlow database bootstrap ────────────");
        Console.WriteLine($"  database        : {r.Database} ({r.Provider})");
        Console.WriteLine($"  existed before  : {(r.DatabaseExistedBefore ? "yes" : "no — created")}");
        Console.WriteLine($"  migrations      : {r.AppliedMigrations.Count} applied, {r.PendingBefore.Count} pending before this run");
        if (r.MigrationsJustApplied.Count > 0)
        {
            foreach (var m in r.MigrationsJustApplied)
            {
                Console.WriteLine($"      + {m}");
            }
        }
        else
        {
            Console.WriteLine("      (schema already up to date)");
        }
        Console.WriteLine($"  baseline seed   : {(r.BaselineSeeded ? "done (roles / Admin)" : "skipped")}");
        if (r.DemoRefusedInProduction)
        {
            Console.WriteLine("  demo seed       : REFUSED (environment is Production)");
        }
        else if (r.DemoSeeded && r.DemoSummary is not null)
        {
            Console.WriteLine("  demo seed       : done");
            foreach (var (entity, tally) in r.DemoSummary.Tallies.OrderBy(t => t.Key))
            {
                Console.WriteLine($"      {entity,-16} +{tally.Created} (skipped {tally.Skipped})");
            }
        }
        else
        {
            Console.WriteLine("  demo seed       : skipped");
        }
        Console.WriteLine("────────────────────────────────────────────────────");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            TaskFlow database bootstrap / demo-data runner

            Usage:
              dotnet run --project src/TaskManager.DbRunner -- <option> [--connection "<conn>"]

            Options:
              --migrate      Create the database if it does not exist and apply all pending EF Core migrations.
              --seed         --migrate, then seed baseline data (roles / local Admin account).
              --seed-demo    --seed, then seed full demo data (users, categories, tags, tasks, subtasks,
                             reminders, recurrence plans, task history). Refused when ASPNETCORE_ENVIRONMENT=Production.
              --all          Alias for --seed-demo.
              --status       Print the migration state and exit without changing anything.
              --help         Show this help.

            Notes:
              * Uses EF Core migrations (Database.Migrate). Never EnsureCreated, never drop / reset / truncate.
              * Every step is idempotent — safe to run repeatedly.
              * Connection string: ConnectionStrings:Postgres (appsettings.json), env var
                ConnectionStrings__Postgres, user-secrets, or --connection "<value>".
              * Defaults to the Development environment; set ASPNETCORE_ENVIRONMENT to change it.
            """);
    }
}

/// <summary>No user context in a CLI run — audit columns fall back to Guid.Empty.</summary>
internal sealed class SystemCurrentUserService : ICurrentUserService
{
    public Guid UserId => Guid.Empty;
    public bool IsAuthenticated => false;
    public bool IsAdmin => false;
}

internal sealed class NoOpNotificationDispatcher : INotificationDispatcher
{
    public Task DispatchAsync(NotificationEntity notification, CancellationToken ct) => Task.CompletedTask;
}
