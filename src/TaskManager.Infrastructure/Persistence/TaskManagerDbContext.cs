using Microsoft.EntityFrameworkCore;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Common;
using TaskManager.Domain.Entities;

namespace TaskManager.Infrastructure.Persistence;

public class TaskManagerDbContext : DbContext, IUnitOfWork
{
    private readonly ICurrentUserService? _currentUser;

    public TaskManagerDbContext(DbContextOptions<TaskManagerDbContext> options, ICurrentUserService? currentUser = null)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserStatusChangeEntity> UserStatusChanges => Set<UserStatusChangeEntity>();
    public DbSet<PasswordResetEntity> PasswordResets => Set<PasswordResetEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<SubtaskEntity> Subtasks => Set<SubtaskEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<TaskTagEntity> TaskTags => Set<TaskTagEntity>();
    public DbSet<ReminderEntity> Reminders => Set<ReminderEntity>();
    public DbSet<RecurrenceRuleEntity> RecurrenceRules => Set<RecurrenceRuleEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<VoiceRecordingEntity> VoiceRecordings => Set<VoiceRecordingEntity>();
    public DbSet<TranscriptionEntity> Transcriptions => Set<TranscriptionEntity>();
    public DbSet<TaskHistoryEntity> TaskHistory => Set<TaskHistoryEntity>();
    public DbSet<AttachmentEntity> Attachments => Set<AttachmentEntity>();
    public DbSet<TaskCommentEntity> TaskComments => Set<TaskCommentEntity>();
    public DbSet<AiConversationEntity> AiConversations => Set<AiConversationEntity>();
    public DbSet<AiMessageEntity> AiMessages => Set<AiMessageEntity>();
    public DbSet<AiUsageEntity> AiUsage => Set<AiUsageEntity>();
    public DbSet<AiDailyPlanEntity> AiDailyPlans => Set<AiDailyPlanEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TaskManagerDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public Task<int> CommitAsync(CancellationToken ct = default) => SaveChangesAsync(ct);

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var userId = SafeUserId();

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<UserOwnedAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedBy == Guid.Empty) entry.Entity.CreatedBy = userId;
                entry.Entity.UpdatedBy = userId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedBy = userId;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    private Guid SafeUserId()
    {
        try
        {
            return _currentUser is { IsAuthenticated: true } ? _currentUser.UserId : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }
}
