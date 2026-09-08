using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TaskManager.Infrastructure.Persistence.Seeding;
using Xunit;

namespace TaskManager.IntegrationTests;

[Collection("api")]
public class DatabaseBootstrapperTests
{
    private readonly ApiFactory _factory;
    public DatabaseBootstrapperTests(ApiFactory factory) => _factory = factory;

    private void RequireDb() => Skip.IfNot(_factory.DatabaseAvailable, "PostgreSQL not reachable on localhost:5432");

    private static readonly TestHostEnvironment Dev = new("Development");

    [SkippableFact]
    public async Task Detects_the_provider_and_reports_the_migration_state()
    {
        RequireDb();

        var status = await DatabaseBootstrapper.RunAsync(
            _factory.Services, Dev, new BootstrapOptions { StatusOnly = true }, NullLogger.Instance);

        status.Provider.Should().Contain("PostgreSQL");
        status.DatabaseExistedBefore.Should().BeTrue();     // ApiFactory migrated it on startup
        status.AppliedMigrations.Should().NotBeEmpty();
        status.PendingBefore.Should().BeEmpty();
        status.MigrationsJustApplied.Should().BeEmpty();
        status.BaselineSeeded.Should().BeFalse();
        status.DemoSeeded.Should().BeFalse();
    }

    [SkippableFact]
    public async Task Applying_migrations_is_idempotent()
    {
        RequireDb();

        var first = await DatabaseBootstrapper.RunAsync(
            _factory.Services, Dev, new BootstrapOptions { ApplyMigrations = true, SeedBaseline = true }, NullLogger.Instance);
        var second = await DatabaseBootstrapper.RunAsync(
            _factory.Services, Dev, new BootstrapOptions { ApplyMigrations = true, SeedBaseline = true }, NullLogger.Instance);

        first.MigrationsJustApplied.Should().BeEmpty("the schema is already current");
        second.MigrationsJustApplied.Should().BeEmpty();
        first.BaselineSeeded.Should().BeTrue();

        // baseline is idempotent — the admin can still log in
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "admin", password = "admin123" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Refuses_demo_seeding_in_Production()
    {
        RequireDb();

        var result = await DatabaseBootstrapper.RunAsync(
            _factory.Services, new TestHostEnvironment("Production"),
            new BootstrapOptions { ApplyMigrations = false, SeedBaseline = false, SeedDemoData = true },
            NullLogger.Instance);

        result.DemoRefusedInProduction.Should().BeTrue();
        result.DemoSeeded.Should().BeFalse();
        result.DemoSummary.Should().BeNull();
    }
}
