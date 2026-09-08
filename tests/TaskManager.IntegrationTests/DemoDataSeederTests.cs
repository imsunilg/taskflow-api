using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Infrastructure.Persistence;
using TaskManager.Infrastructure.Persistence.Seeding;
using Xunit;

namespace TaskManager.IntegrationTests;

[Collection("api")]
public class DemoDataSeederTests
{
    private readonly ApiFactory _factory;
    public DemoDataSeederTests(ApiFactory factory) => _factory = factory;

    private void RequireDb() => Skip.IfNot(_factory.DatabaseAvailable, "PostgreSQL not reachable on localhost:5432");

    private static IConfiguration Config(params (string, string?)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(v => v.Item1, v => v.Item2)).Build();

    private async Task InScopeAsync(Func<TaskManagerDbContext, IPasswordHasher, Task> body)
    {
        using var scope = _factory.Services.CreateScope();
        await body(
            scope.ServiceProvider.GetRequiredService<TaskManagerDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>());
    }

    [Fact]
    public void Disabled_by_default_and_only_enabled_by_an_explicit_switch()
    {
        DemoDataSeeder.IsEnabled(Config(), Array.Empty<string>()).Should().BeFalse();
        DemoDataSeeder.IsEnabled(Config(("SeedDemoData", "false")), Array.Empty<string>()).Should().BeFalse();

        DemoDataSeeder.IsEnabled(Config(("SeedDemoData", "true")), Array.Empty<string>()).Should().BeTrue();
        DemoDataSeeder.IsEnabled(Config(), new[] { "--seed-demo-data" }).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Refuses_to_run_in_Production_even_when_the_switch_is_on()
    {
        RequireDb();
        int before = 0;
        await InScopeAsync(async (db, _) => before = await db.Users.IgnoreQueryFilters().CountAsync());

        await DemoDataSeeder.RunAsync(_factory.Services, new TestHostEnvironment("Production"), new[] { "--seed-demo-data" });

        await InScopeAsync(async (db, _) =>
            (await db.Users.IgnoreQueryFilters().CountAsync()).Should().Be(before));
    }

    [SkippableFact]
    public async Task Seeds_a_coherent_dataset_that_is_idempotent_and_covers_the_6_month_range()
    {
        RequireDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await InScopeAsync(async (db, hasher) =>
        {
            var first = await DemoDataSeeder.SeedAsync(db, hasher, NullLogger.Instance, today);
            first.CreatedOf("users").Should().BeGreaterThan(5);
            first.CreatedOf("tasks").Should().BeGreaterThan(300);
        });

        Dictionary<string, int> after1 = new();
        await InScopeAsync(async (db, _) => after1 = await SnapshotAsync(db));

        await InScopeAsync(async (db, hasher) =>
        {
            var second = await DemoDataSeeder.SeedAsync(db, hasher, NullLogger.Instance, today);
            second.AnythingCreated.Should().BeFalse("re-running the seed must not create duplicates");
        });

        await InScopeAsync(async (db, _) =>
        {
            (await SnapshotAsync(db)).Should().BeEquivalentTo(after1);

            var demoUserIds = await db.Users.IgnoreQueryFilters()
                .Where(u => u.Email.EndsWith("@taskflow.local") || u.Email == "demo@taskflow.app")
                .Select(u => u.Id).ToListAsync();

            var tasks = await db.Tasks.IgnoreQueryFilters()
                .Where(t => demoUserIds.Contains(t.UserId))
                .Select(t => new { t.Id, t.CategoryId, t.DueDate, t.RecurrenceRuleId, t.Completed })
                .ToListAsync();
            tasks.Should().NotBeEmpty();

            var categoryIds = (await db.Categories.Where(c => demoUserIds.Contains(c.UserId)).Select(c => c.Id).ToListAsync()).ToHashSet();
            tasks.Where(t => t.CategoryId != null).Should().OnlyContain(t => categoryIds.Contains(t.CategoryId!.Value));

            var ruleIds = (await db.RecurrenceRules.Select(r => r.Id).ToListAsync()).ToHashSet();
            tasks.Where(t => t.RecurrenceRuleId != null).Should().OnlyContain(t => ruleIds.Contains(t.RecurrenceRuleId!.Value));

            var taskIds = tasks.Select(t => t.Id).ToHashSet();
            var subtaskParents = await db.Set<SubtaskEntity>().Select(s => s.TaskId).Distinct().ToListAsync();
            subtaskParents.Where(taskIds.Contains).Should().OnlyContain(id => taskIds.Contains(id));
            var reminderParents = await db.Reminders.Select(r => r.TaskId).ToListAsync();
            reminderParents.Where(taskIds.Contains).Should().OnlyContain(id => taskIds.Contains(id));

            var due = tasks.Where(t => t.DueDate != null).Select(t => t.DueDate!.Value).ToList();
            due.Min().Should().BeOnOrBefore(today.AddMonths(-2));
            due.Max().Should().BeOnOrAfter(today.AddMonths(2));

            tasks.Count(t => t.Completed).Should().BeGreaterThan(0);
            tasks.Count(t => !t.Completed && t.DueDate < today).Should().BeGreaterThan(0);
            tasks.Count(t => t.DueDate == null).Should().BeGreaterThan(0);
            tasks.Count(t => t.RecurrenceRuleId != null).Should().BeGreaterThan(0);
        });
    }

    [SkippableFact]
    public async Task Seeded_active_users_can_log_in_and_deactivated_ones_cannot()
    {
        RequireDb();
        await InScopeAsync((db, hasher) =>
            DemoDataSeeder.SeedAsync(db, hasher, NullLogger.Instance, DateOnly.FromDateTime(DateTime.UtcNow)));

        var client = _factory.CreateClient();

        var active = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "ava.chen@taskflow.local", password = DemoDataSeeder.LocalUserPassword });
        active.StatusCode.Should().Be(HttpStatusCode.OK);

        var deactivated = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "ethan.novak@taskflow.local", password = DemoDataSeeder.LocalUserPassword });
        deactivated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await deactivated.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("message").GetString().Should().Contain("inactive");
    }

    private static async Task<Dictionary<string, int>> SnapshotAsync(TaskManagerDbContext db) => new()
    {
        ["users"] = await db.Users.IgnoreQueryFilters().CountAsync(),
        ["categories"] = await db.Categories.CountAsync(),
        ["tags"] = await db.Tags.CountAsync(),
        ["tasks"] = await db.Tasks.IgnoreQueryFilters().CountAsync(),
        ["subtasks"] = await db.Set<SubtaskEntity>().CountAsync(),
        ["taskTags"] = await db.TaskTags.CountAsync(),
        ["reminders"] = await db.Reminders.CountAsync(),
        ["recurrenceRules"] = await db.RecurrenceRules.CountAsync(),
        ["history"] = await db.TaskHistory.CountAsync(),
        ["voice"] = await db.VoiceRecordings.CountAsync(),
    };
}
