using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Infrastructure.Persistence.Seeding;

/// <summary>
/// Baseline seeding: ensures the local Admin account exists and, in Development,
/// a single demo user with a couple of starter categories/tasks. Idempotent.
/// Never modifies or deletes existing rows.
/// </summary>
public static class DbSeeder
{
    private const string DefaultAdminPassword = "admin123";

    public static async Task SeedAsync(IServiceProvider services, IHostEnvironment env)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskManagerDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbSeeder");

        // Auto-migrate in development only; staging/production run migrations as an
        // explicit CI/CD step (or via TaskManager.DbRunner) before the new API rolls out.
        if (env.IsDevelopment())
        {
            await db.Database.MigrateAsync();
        }

        await SeedAdminAsync(db, hasher, config, env, logger);

        if (!env.IsDevelopment() || await db.Users.AnyAsync(u => u.Role != UserRole.Admin))
        {
            return;
        }

        var user = new UserEntity
        {
            Email = "demo@taskflow.app",
            DisplayName = "Demo User",
            PasswordHash = hasher.Hash("password123"),
            Role = UserRole.User,
        };
        db.Users.Add(user);

        var work = new CategoryEntity { UserId = user.Id, Name = "Work", Color = "#3b82f6" };
        var personal = new CategoryEntity { UserId = user.Id, Name = "Personal", Color = "#16a34a" };
        db.Categories.AddRange(work, personal);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Tasks.AddRange(
            new TaskEntity
            {
                UserId = user.Id, Title = "Review sprint board", Priority = Priority.High,
                DueDate = today, CategoryId = work.Id, CreatedBy = user.Id, UpdatedBy = user.Id,
            },
            new TaskEntity
            {
                UserId = user.Id, Title = "Book dentist appointment", Priority = Priority.Medium,
                DueDate = today.AddDays(2), CategoryId = personal.Id, CreatedBy = user.Id, UpdatedBy = user.Id,
            },
            new TaskEntity
            {
                UserId = user.Id, Title = "Renew cloud certification", Priority = Priority.Low,
                Important = true, CreatedBy = user.Id, UpdatedBy = user.Id,
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Ensures an Admin account exists. Credentials come from configuration
    /// (<c>Admin:Email</c> / <c>Admin:Password</c>, env vars <c>Admin__Email</c> /
    /// <c>Admin__Password</c>, or user-secrets). The password is only ever stored
    /// as a PBKDF2 hash — never in plain text.
    /// </summary>
    public static async Task SeedAdminAsync(
        TaskManagerDbContext db,
        IPasswordHasher hasher,
        IConfiguration config,
        IHostEnvironment env,
        ILogger logger)
    {
        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
        {
            return; // never overwrite an existing admin's credentials
        }

        var email = config["Admin:Email"] ?? "admin";
        var displayName = config["Admin:DisplayName"] ?? "Administrator";
        var password = config["Admin:Password"] ?? DefaultAdminPassword;

        if (!env.IsDevelopment() && password == DefaultAdminPassword)
        {
            logger.LogWarning(
                "Admin account NOT seeded: refusing to create the default admin password outside Development. " +
                "Set Admin__Password to a strong secret and re-run, or provision the admin in your deploy pipeline.");
            return;
        }

        db.Users.Add(new UserEntity
        {
            Email = email.Trim().ToLowerInvariant(),
            DisplayName = displayName,
            PasswordHash = hasher.Hash(password),
            Role = UserRole.Admin,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded Admin account '{Email}'.", email);
    }
}
