using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;
namespace TaskManager.Infrastructure.Persistence.Seeding;

/// <summary>Per-entity created / skipped tallies for console output.</summary>
public sealed class DemoSeedSummary
{
    private readonly Dictionary<string, (int Created, int Skipped)> _tallies = new();

    public IReadOnlyDictionary<string, (int Created, int Skipped)> Tallies => _tallies;

    public void Add(string entity, int created, int skipped)
    {
        var current = _tallies.GetValueOrDefault(entity);
        _tallies[entity] = (current.Created + created, current.Skipped + skipped);
    }

    public int CreatedOf(string entity) => _tallies.GetValueOrDefault(entity).Created;
    public bool AnythingCreated => _tallies.Values.Any(v => v.Created > 0);
}

/// <summary>
/// DEVELOPMENT / DEMO ONLY. Populates the database with realistic dummy users and
/// 6 months of task-management data (previous 3 months → next 3 months).
///
/// - Never runs in Production.
/// - Off by default; enable with config <c>SeedDemoData=true</c>, env <c>SEED_DEMO_DATA=true</c>,
///   or the CLI arg <c>--seed-demo-data</c>.
/// - Idempotent: every row uses a deterministic id (or natural-key lookup), so re-running
///   creates nothing new and never touches pre-existing / real data.
/// - Passwords are hashed with the app's <see cref="IPasswordHasher"/> and never logged.
/// </summary>
public static class DemoDataSeeder
{
    public const string ConfigKey = "SeedDemoData";

    /// <summary>Shared password for the generated <c>@taskflow.local</c> accounts (documented in Documents/DEMO-DATA.md).</summary>
    public const string LocalUserPassword = "Demo!Pass1";

    private const int TasksPerActiveUser = 64;
    private const int TasksPerInactiveUser = 6;

    public static bool IsEnabled(IConfiguration config, string[] args) =>
        config.GetValue(ConfigKey, false)
        || (Environment.GetEnvironmentVariable("SEED_DEMO_DATA") ?? string.Empty)
            .Equals("true", StringComparison.OrdinalIgnoreCase)
        || args.Any(a => a.Equals("--seed-demo-data", StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync(IServiceProvider services, IHostEnvironment env, string[] args)
    {
        using var scope = services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DemoDataSeeder");

        if (!IsEnabled(config, args))
        {
            return;
        }

        if (env.IsProduction())
        {
            logger.LogWarning(
                "SeedDemoData is enabled but the environment is Production. Demo seeding is REFUSED — " +
                "no data was written.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<TaskManagerDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        logger.LogInformation("DemoDataSeeder starting ({Environment})…", env.EnvironmentName);
        var summary = await SeedAsync(db, hasher, logger, DateOnly.FromDateTime(DateTime.UtcNow));
        LogSummary(logger, summary);
    }

    private static void LogSummary(ILogger logger, DemoSeedSummary summary)
    {
        logger.LogInformation("DemoDataSeeder summary (created / skipped):");
        foreach (var (entity, tally) in summary.Tallies.OrderBy(t => t.Key))
        {
            logger.LogInformation("  {Entity,-16} +{Created,-5} ({Skipped} already present)", entity, tally.Created, tally.Skipped);
        }
        if (!summary.AnythingCreated)
        {
            logger.LogInformation("  (nothing new — the database already has the demo data)");
        }
    }

    // ----------------------------------------------------------------------------------

    public static async Task<DemoSeedSummary> SeedAsync(
        TaskManagerDbContext db, IPasswordHasher hasher, ILogger logger, DateOnly today, CancellationToken ct = default)
    {
        var summary = new DemoSeedSummary();
        var rangeStart = today.AddMonths(-3);
        var rangeEnd = today.AddMonths(3);

        var users = await EnsureUsersAsync(db, hasher, summary, ct);

        foreach (var (user, spec) in users)
        {
            var cats = await EnsureCategoriesAsync(db, user.Id, summary, ct);
            var tags = await EnsureTagsAsync(db, user.Id, summary, ct);
            var count = spec.IsActive ? TasksPerActiveUser : TasksPerInactiveUser;

            await SeedTasksForUserAsync(db, user, cats, tags, count, today, rangeStart, rangeEnd, summary, ct);

            if (spec.IsActive)
            {
                await SeedRecurringForUserAsync(db, user, cats, today, rangeEnd, summary, ct);
            }
        }

        logger.LogInformation(
            "DemoDataSeeder: due dates span {Start:yyyy-MM-dd} → {End:yyyy-MM-dd}", rangeStart, rangeEnd);
        return summary;
    }

    // ---- users -----------------------------------------------------------------------

    private sealed record UserSpec(string Email, string DisplayName, string Password, UserRole Role, bool IsActive);

    private static IReadOnlyList<UserSpec> DemoUserSpecs() => new List<UserSpec>
    {
        // Existing project credentials — created only if missing, never modified.
        new("admin", "Administrator", "admin123", UserRole.Admin, true),
        new("demo@taskflow.app", "Demo User", "password123", UserRole.User, true),

        // Additional realistic demo users on a clearly-local domain.
        new("ava.chen@taskflow.local", "Ava Chen", LocalUserPassword, UserRole.User, true),
        new("liam.patel@taskflow.local", "Liam Patel", LocalUserPassword, UserRole.User, true),
        new("mia.rossi@taskflow.local", "Mia Rossi", LocalUserPassword, UserRole.User, true),
        new("noah.kim@taskflow.local", "Noah Kim", LocalUserPassword, UserRole.User, true),
        new("sofia.garcia@taskflow.local", "Sofia Garcia", LocalUserPassword, UserRole.User, true),
        new("grace.osei@taskflow.local", "Grace Osei", LocalUserPassword, UserRole.Admin, true),
        new("ethan.novak@taskflow.local", "Ethan Novak", LocalUserPassword, UserRole.User, false),
        new("priya.nair@taskflow.local", "Priya Nair", LocalUserPassword, UserRole.User, false),
    };

    private static async Task<List<(UserEntity User, UserSpec Spec)>> EnsureUsersAsync(
        TaskManagerDbContext db, IPasswordHasher hasher, DemoSeedSummary summary, CancellationToken ct)
    {
        var specs = DemoUserSpecs();
        var emails = specs.Select(s => s.Email.Trim().ToLowerInvariant()).ToList();
        var existing = await db.Users.IgnoreQueryFilters()
            .Where(u => emails.Contains(u.Email))
            .ToListAsync(ct);

        var result = new List<(UserEntity, UserSpec)>();
        var created = 0;
        var skipped = 0;

        foreach (var spec in specs)
        {
            var email = spec.Email.Trim().ToLowerInvariant();
            var match = existing.FirstOrDefault(u => u.Email == email);
            if (match is not null)
            {
                // Pre-existing (or already-seeded) — never modify it.
                skipped++;
                if (!match.IsDeleted)
                {
                    result.Add((match, spec));
                }
                continue;
            }

            var user = new UserEntity
            {
                Id = DemoGuid("user", email),
                Email = email,
                DisplayName = spec.DisplayName,
                PasswordHash = hasher.Hash(spec.Password),
                Role = spec.Role,
                IsActive = spec.IsActive,
                DeactivatedAt = spec.IsActive ? null : DateTime.UtcNow.AddDays(-20),
            };
            db.Users.Add(user);
            result.Add((user, spec));
            created++;
        }

        await db.SaveChangesAsync(ct);
        summary.Add("users", created, skipped);
        return result;
    }

    // ---- categories & tags ---------------------------------------------------------

    private static readonly (string Name, string Color, string Icon)[] CategorySpecs =
    {
        ("Personal", "#16a34a", "user"),
        ("Work", "#3b82f6", "briefcase"),
        ("Study", "#8b5cf6", "book"),
        ("Finance", "#f59e0b", "wallet"),
        ("Health", "#ef4444", "heart"),
        ("Shopping", "#ec4899", "cart"),
        ("Projects", "#0ea5e9", "layers"),
        ("Travel", "#14b8a6", "plane"),
    };

    private static readonly string[] TagSpecs =
        { "urgent", "meeting", "backend", "frontend", "study", "home", "finance", "followup" };

    private static async Task<Dictionary<string, Guid>> EnsureCategoriesAsync(
        TaskManagerDbContext db, Guid userId, DemoSeedSummary summary, CancellationToken ct)
    {
        var existing = await db.Categories.Where(c => c.UserId == userId)
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var byName = existing.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;
        foreach (var (name, color, icon) in CategorySpecs)
        {
            if (byName.ContainsKey(name))
            {
                skipped++;
                continue;
            }
            var id = DemoGuid("cat", userId, name);
            db.Categories.Add(new CategoryEntity { Id = id, UserId = userId, Name = name, Color = color, Icon = icon });
            byName[name] = id;
            created++;
        }

        await db.SaveChangesAsync(ct);
        summary.Add("categories", created, skipped);
        return byName;
    }

    private static async Task<Dictionary<string, Guid>> EnsureTagsAsync(
        TaskManagerDbContext db, Guid userId, DemoSeedSummary summary, CancellationToken ct)
    {
        var existing = await db.Tags.Where(t => t.UserId == userId)
            .Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var byName = existing.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var skipped = 0;
        foreach (var name in TagSpecs)
        {
            if (byName.ContainsKey(name))
            {
                skipped++;
                continue;
            }
            var id = DemoGuid("tag", userId, name);
            db.Tags.Add(new TagEntity { Id = id, UserId = userId, Name = name, CreatedAt = DateTime.UtcNow });
            byName[name] = id;
            created++;
        }

        await db.SaveChangesAsync(ct);
        summary.Add("tags", created, skipped);
        return byName;
    }

    // ---- tasks -------------------------------------------------------------------------

    private static readonly Dictionary<string, string[]> TitlesByCategory = new()
    {
        ["Work"] = new[]
        {
            "Review sprint board", "Submit weekly status report", "Prepare project presentation",
            "Follow up with client", "Stand-up preparation", "Draft release notes",
            "Update roadmap document", "Review pull requests", "Plan next sprint",
            "Sync with design team", "Write incident post-mortem", "Refine backlog",
        },
        ["Personal"] = new[]
        {
            "Book dentist appointment", "Call the plumber", "Renew driving licence",
            "Water the plants", "Tidy the garage", "Plan weekend trip", "Call parents",
            "Fix the leaking tap", "Back up laptop", "Sort out mail pile",
        },
        ["Study"] = new[]
        {
            "Study Angular routing", "Read chapter on EF Core", "Practice SQL joins",
            "Complete TypeScript course module", "Review system design notes",
            "Write flashcards for exam", "Watch talk on distributed systems", "Do coding kata",
        },
        ["Finance"] = new[]
        {
            "Pay electricity bill", "Review monthly expenses", "Renew insurance document",
            "Transfer to savings", "Reconcile bank statement", "File expense claim",
            "Check credit card statement", "Update budget spreadsheet",
        },
        ["Health"] = new[]
        {
            "Morning run", "Book annual check-up", "Schedule eye test", "Meal prep for the week",
            "Pick up prescription", "30-minute walk", "Stretching routine",
        },
        ["Shopping"] = new[]
        {
            "Grocery shopping", "Buy birthday gift", "Order printer ink", "Replace toothbrush heads",
            "Buy running shoes", "Restock pantry", "Order new charger",
        },
        ["Projects"] = new[]
        {
            "Set up CI pipeline", "Write project README", "Design database schema",
            "Prototype landing page", "Add unit tests", "Deploy to staging",
            "Collect user feedback", "Refactor auth module",
        },
        ["Travel"] = new[]
        {
            "Book flights", "Renew passport", "Reserve hotel", "Plan itinerary",
            "Arrange airport transfer", "Buy travel insurance", "Pack suitcase",
        },
    };

    private static readonly string[] NoteSnippets =
    {
        "Check with the team before finalising.",
        "Blocked until the vendor replies.",
        "Low effort — should take 20 minutes.",
        "Needs sign-off from finance.",
        "Draft is in the shared drive.",
        "Remember to attach the invoice.",
        "Discussed on the last call — see notes.",
    };

    private static readonly string[] SubtaskSnippets =
    {
        "Gather requirements", "Draft outline", "Review with stakeholder", "Incorporate feedback",
        "Final proofread", "Send for approval", "Book the room", "Prepare slides",
        "Collect figures", "Update the tracker", "Confirm attendees", "Test the demo",
    };

    private static readonly string[] VoiceTranscripts =
    {
        "remind me to call the bank tomorrow at 3pm",
        "add buy milk and eggs to the shopping list",
        "schedule a dentist appointment next week high priority",
        "note follow up with the client about the proposal",
    };

    private static async Task SeedTasksForUserAsync(
        TaskManagerDbContext db, UserEntity user,
        Dictionary<string, Guid> cats, Dictionary<string, Guid> tags,
        int count, DateOnly today, DateOnly rangeStart, DateOnly rangeEnd,
        DemoSeedSummary summary, CancellationToken ct)
    {
        var rng = new Random(StableSeed(user.Email));

        var wantedIds = Enumerable.Range(0, count).Select(i => DemoGuid("task", user.Id, i)).ToList();
        var existingTaskIds = await db.Tasks.IgnoreQueryFilters()
            .Where(t => wantedIds.Contains(t.Id)).Select(t => t.Id).ToHashSetAsync(ct);

        var newTasks = new List<TaskEntity>();
        var newSubtasks = new List<SubtaskEntity>();
        var newTaskTags = new List<TaskTagEntity>();
        var newReminders = new List<ReminderEntity>();
        var newHistory = new List<TaskHistoryEntity>();
        var newVoice = new List<VoiceRecordingEntity>();
        var newTranscripts = new List<TranscriptionEntity>();

        var catNames = CategorySpecs.Select(c => c.Name).ToArray();
        var tagNames = TagSpecs;

        for (var i = 0; i < count; i++)
        {
            var id = wantedIds[i];
            if (existingTaskIds.Contains(id))
            {
                continue;
            }

            var catName = catNames[rng.Next(catNames.Length)];
            var titles = TitlesByCategory[catName];
            var title = titles[rng.Next(titles.Length)];

            var bucket = rng.Next(100);
            DateOnly? dueDate;
            bool completed = false;
            DateTime? completedAt = null;

            if (bucket < 34) // completed in the past
            {
                dueDate = RandomDate(rng, rangeStart, today.AddDays(-1));
                completed = true;
                completedAt = Utc(dueDate.Value, new TimeOnly(rng.Next(8, 19), rng.Next(0, 60)));
            }
            else if (bucket < 44) // overdue, still open
            {
                dueDate = RandomDate(rng, today.AddDays(-21), today.AddDays(-1));
            }
            else if (bucket < 50) // due today
            {
                dueDate = today;
            }
            else if (bucket < 68) // soon
            {
                dueDate = RandomDate(rng, today.AddDays(1), today.AddDays(14));
            }
            else if (bucket < 85) // later this quarter
            {
                dueDate = RandomDate(rng, today.AddDays(15), rangeEnd);
            }
            else // no due date
            {
                dueDate = null;
            }

            var priority = WeightedPriority(rng);
            var important = rng.Next(100) < 15 || priority == Priority.Urgent;
            TimeOnly? dueTime = dueDate is not null && rng.Next(100) < 45
                ? new TimeOnly(new[] { 9, 10, 12, 14, 15, 17, 18 }[rng.Next(7)], new[] { 0, 15, 30 }[rng.Next(3)])
                : null;

            var isVoice = i < VoiceTranscripts.Length && rng.Next(100) < 40;

            var task = new TaskEntity
            {
                Id = id,
                UserId = user.Id,
                Title = title,
                Description = rng.Next(100) < 35 ? $"{title} — details captured during planning." : null,
                DueDate = dueDate,
                DueTime = dueTime,
                Priority = priority,
                CategoryId = cats.GetValueOrDefault(catName),
                Completed = completed,
                CompletedAt = completedAt,
                Important = important,
                Notes = rng.Next(100) < 40 ? NoteSnippets[rng.Next(NoteSnippets.Length)] : null,
                IsVoiceCreated = isVoice,
                CreatedBy = user.Id,
                UpdatedBy = user.Id,
            };
            newTasks.Add(task);

            // tags: 1–3
            var tagCount = 1 + rng.Next(3);
            foreach (var tagName in Shuffle(rng, tagNames).Take(tagCount))
            {
                if (tags.TryGetValue(tagName, out var tagId))
                {
                    newTaskTags.Add(new TaskTagEntity { TaskId = id, TagId = tagId });
                }
            }

            // subtasks: 0–5 (≈45% have some)
            if (rng.Next(100) < 45)
            {
                var subs = 1 + rng.Next(5);
                for (var s = 0; s < subs; s++)
                {
                    newSubtasks.Add(new SubtaskEntity
                    {
                        Id = DemoGuid("sub", id, s),
                        TaskId = id,
                        Text = SubtaskSnippets[rng.Next(SubtaskSnippets.Length)],
                        IsDone = completed || rng.Next(100) < 30,
                        SortOrder = s,
                        CreatedAt = DateTime.UtcNow,
                    });
                }
            }

            // reminder for dated + timed tasks (≈35%)
            if (dueDate is not null && dueTime is not null && rng.Next(100) < 35)
            {
                var offset = new[] { 0, 10, 30, 60, 1440 }[rng.Next(5)];
                var firesAt = Utc(dueDate.Value, dueTime.Value).AddMinutes(-offset);
                newReminders.Add(new ReminderEntity
                {
                    Id = DemoGuid("rem", id),
                    TaskId = id,
                    OffsetMinutes = offset,
                    FiresAt = firesAt,
                    DeliveredAt = firesAt < DateTime.UtcNow ? firesAt.AddMinutes(1) : null,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            // history for completed tasks
            if (completed)
            {
                newHistory.Add(new TaskHistoryEntity
                {
                    Id = DemoGuid("hist", id, "create"),
                    TaskId = id,
                    ChangeText = "Task created",
                    ChangedAt = completedAt!.Value.AddDays(-rng.Next(1, 10)),
                    ChangedBy = user.Id,
                });
                newHistory.Add(new TaskHistoryEntity
                {
                    Id = DemoGuid("hist", id, "done"),
                    TaskId = id,
                    ChangeText = "Marked complete",
                    ChangedAt = completedAt.Value,
                    ChangedBy = user.Id,
                });
            }

            if (isVoice)
            {
                var vrId = DemoGuid("voice", id);
                newVoice.Add(new VoiceRecordingEntity
                {
                    Id = vrId,
                    TaskId = id,
                    UserId = user.Id,
                    AudioUrl = null,
                    DurationSeconds = 3 + rng.Next(10),
                    CreatedAt = DateTime.UtcNow,
                });
                newTranscripts.Add(new TranscriptionEntity
                {
                    Id = DemoGuid("transcript", id),
                    VoiceRecordingId = vrId,
                    RawText = VoiceTranscripts[i % VoiceTranscripts.Length],
                    Provider = "Demo",
                    ExtractedJson = "{}",
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        db.Tasks.AddRange(newTasks);
        await db.SaveChangesAsync(ct);

        // Children reference the tasks just saved. All ids are deterministic so re-runs
        // find them already present; the task-level skip above usually short-circuits this.
        var subtaskRows = await FilterNewByIdAsync(db.Set<SubtaskEntity>(), newSubtasks, s => s.Id, ct);
        var reminderRows = await FilterNewByIdAsync(db.Set<ReminderEntity>(), newReminders, r => r.Id, ct);
        var historyRows = await FilterNewByIdAsync(db.Set<TaskHistoryEntity>(), newHistory, h => h.Id, ct);
        var voiceRows = await FilterNewByIdAsync(db.Set<VoiceRecordingEntity>(), newVoice, v => v.Id, ct);
        var transcriptRows = await FilterNewByIdAsync(db.Set<TranscriptionEntity>(), newTranscripts, t => t.Id, ct);
        var taskTagRows = await FilterNewTaskTagsAsync(db, newTaskTags, ct);

        db.Set<SubtaskEntity>().AddRange(subtaskRows);
        db.Set<ReminderEntity>().AddRange(reminderRows);
        db.Set<TaskHistoryEntity>().AddRange(historyRows);
        db.Set<VoiceRecordingEntity>().AddRange(voiceRows);
        db.Set<TranscriptionEntity>().AddRange(transcriptRows);
        db.TaskTags.AddRange(taskTagRows);
        await db.SaveChangesAsync(ct);

        summary.Add("tasks", newTasks.Count, count - newTasks.Count);
        summary.Add("subtasks", subtaskRows.Count, 0);
        summary.Add("taskTags", taskTagRows.Count, 0);
        summary.Add("reminders", reminderRows.Count, 0);
        summary.Add("history", historyRows.Count, 0);
        summary.Add("voiceRecordings", voiceRows.Count, 0);
    }

    private static async Task<List<T>> FilterNewByIdAsync<T>(
        DbSet<T> set, List<T> candidates, Func<T, Guid> id, CancellationToken ct) where T : class
    {
        if (candidates.Count == 0) return candidates;
        var ids = candidates.Select(id).ToList();
        var existing = (await set.Where(e => ids.Contains(EF.Property<Guid>(e, "Id")))
            .Select(e => EF.Property<Guid>(e, "Id")).ToListAsync(ct)).ToHashSet();
        return candidates.Where(c => !existing.Contains(id(c))).ToList();
    }

    private static async Task<List<TaskTagEntity>> FilterNewTaskTagsAsync(
        TaskManagerDbContext db, List<TaskTagEntity> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return candidates;
        var taskIds = candidates.Select(c => c.TaskId).Distinct().ToList();
        var existing = (await db.TaskTags.Where(tt => taskIds.Contains(tt.TaskId))
                .Select(tt => new { tt.TaskId, tt.TagId }).ToListAsync(ct))
            .Select(x => (x.TaskId, x.TagId)).ToHashSet();

        var result = new List<TaskTagEntity>();
        var seen = new HashSet<(Guid, Guid)>();
        foreach (var tt in candidates)
        {
            var key = (tt.TaskId, tt.TagId);
            if (existing.Contains(key) || !seen.Add(key)) continue;
            result.Add(tt);
        }
        return result;
    }

    // ---- recurring plans -------------------------------------------------------------

    private sealed record RecurrenceSpec(string Key, string Title, string Category, RecurrenceFrequency Frequency, int[]? Days, int? DayOfMonth);

    private static readonly RecurrenceSpec[] RecurrenceSpecs =
    {
        new("daily-plan", "Review daily plan", "Personal", RecurrenceFrequency.Daily, null, null),
        new("exercise", "Exercise for 30 minutes", "Health", RecurrenceFrequency.Daily, null, null),
        new("check-email", "Check email and triage", "Work", RecurrenceFrequency.Weekdays, null, null),
        new("standup", "Stand-up preparation", "Work", RecurrenceFrequency.Weekdays, null, null),
        new("weekly-plan", "Weekly planning", "Work", RecurrenceFrequency.Weekly, new[] { 1 }, null),
        new("expense-review", "Review monthly expenses", "Finance", RecurrenceFrequency.Monthly, null, 1),
        new("insurance", "Renew insurance document", "Finance", RecurrenceFrequency.Yearly, null, null),
    };

    private static async Task SeedRecurringForUserAsync(
        TaskManagerDbContext db, UserEntity user, Dictionary<string, Guid> cats,
        DateOnly today, DateOnly rangeEnd, DemoSeedSummary summary, CancellationToken ct)
    {
        var ruleIds = RecurrenceSpecs.Select(s => DemoGuid("rr", user.Id, s.Key)).ToList();
        var taskIds = RecurrenceSpecs.Select(s => DemoGuid("rtask", user.Id, s.Key)).ToList();

        var existingRules = await db.RecurrenceRules.Where(r => ruleIds.Contains(r.Id))
            .Select(r => r.Id).ToHashSetAsync(ct);
        var existingTasks = await db.Tasks.IgnoreQueryFilters()
            .Where(t => taskIds.Contains(t.Id)).Select(t => t.Id).ToHashSetAsync(ct);

        var rulesCreated = 0;
        var tasksCreated = 0;

        for (var i = 0; i < RecurrenceSpecs.Length; i++)
        {
            var spec = RecurrenceSpecs[i];
            var ruleId = ruleIds[i];
            var taskId = taskIds[i];

            if (!existingRules.Contains(ruleId))
            {
                db.RecurrenceRules.Add(new RecurrenceRuleEntity
                {
                    Id = ruleId,
                    Frequency = spec.Frequency,
                    Interval = 1,
                    DaysOfWeek = spec.Days,
                    DayOfMonth = spec.DayOfMonth,
                    Until = rangeEnd,
                    CreatedAt = DateTime.UtcNow,
                });
                rulesCreated++;
            }

            if (!existingTasks.Contains(taskId))
            {
                var due = spec.Frequency switch
                {
                    RecurrenceFrequency.Monthly => new DateOnly(today.Year, today.Month, 1).AddMonths(1),
                    RecurrenceFrequency.Yearly => today.AddMonths(2),
                    RecurrenceFrequency.Weekly => NextWeekday(today, DayOfWeek.Monday),
                    _ => today,
                };
                db.Tasks.Add(new TaskEntity
                {
                    Id = taskId,
                    UserId = user.Id,
                    Title = spec.Title,
                    Priority = Priority.Medium,
                    DueDate = due,
                    DueTime = new TimeOnly(9, 0),
                    CategoryId = cats.GetValueOrDefault(spec.Category),
                    RecurrenceRuleId = ruleId,
                    Notes = "Recurring demo task.",
                    CreatedBy = user.Id,
                    UpdatedBy = user.Id,
                });
                tasksCreated++;
            }
        }

        await db.SaveChangesAsync(ct);
        summary.Add("recurrenceRules", rulesCreated, RecurrenceSpecs.Length - rulesCreated);
        summary.Add("recurringTasks", tasksCreated, RecurrenceSpecs.Length - tasksCreated);
    }

    // ---- helpers -------------------------------------------------------------------

    private static Guid DemoGuid(params object[] parts)
    {
        var key = "taskflow-demo/" + string.Join("/", parts.Select(p => p?.ToString() ?? string.Empty));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    private static int StableSeed(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("seed/" + value));
        return BitConverter.ToInt32(hash, 0);
    }

    private static DateTime Utc(DateOnly date, TimeOnly time) =>
        DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Utc);

    private static DateOnly RandomDate(Random rng, DateOnly from, DateOnly to)
    {
        var span = to.DayNumber - from.DayNumber;
        return span <= 0 ? from : from.AddDays(rng.Next(span + 1));
    }

    private static Priority WeightedPriority(Random rng) => rng.Next(100) switch
    {
        < 20 => Priority.Low,
        < 62 => Priority.Medium,
        < 88 => Priority.High,
        _ => Priority.Urgent,
    };

    private static DateOnly NextWeekday(DateOnly from, DayOfWeek day)
    {
        var delta = ((int)day - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(delta == 0 ? 7 : delta);
    }

    private static IEnumerable<T> Shuffle<T>(Random rng, IReadOnlyList<T> source)
    {
        var copy = source.ToArray();
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }
}
