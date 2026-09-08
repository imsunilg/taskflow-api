using TaskManager.Application.Admin;
using TaskManager.Application.Common;
using TaskManager.Application.Tasks;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Interfaces;

/// <summary>Minimal per-request auth check payload: is the account usable, and its current security stamp.</summary>
public record UserSecuritySnapshot(bool IsActive, string SecurityStamp);

public interface ITaskRepository
{
    Task<PagedResult<TaskEntity>> GetPagedAsync(Guid userId, TaskQueryParameters query, CancellationToken ct);
    Task<TaskEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct);
    Task AddAsync(TaskEntity task, CancellationToken ct);
    Task<IReadOnlyList<TaskEntity>> GetTodayAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TaskEntity>> GetUpcomingAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TaskEntity>> GetOverdueAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TaskEntity>> GetImportantAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TaskEntity>> GetInboxAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<TaskEntity>> GetCalendarAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PagedResult<TaskEntity>> SearchAsync(Guid userId, string term, int page, int pageSize, CancellationToken ct);
}

public interface ICategoryRepository
{
    Task<IReadOnlyList<CategoryEntity>> GetAllAsync(Guid userId, CancellationToken ct);
    Task<CategoryEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct);
    Task AddAsync(CategoryEntity category, CancellationToken ct);
    void Remove(CategoryEntity category);
    Task<bool> NameExistsAsync(Guid userId, string name, Guid? excludeId, CancellationToken ct);
}

public interface ITagRepository
{
    Task<IReadOnlyList<TagEntity>> GetAllAsync(Guid userId, CancellationToken ct);
    Task<TagEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct);
    Task AddAsync(TagEntity tag, CancellationToken ct);
    void Remove(TagEntity tag);
    Task<IReadOnlyList<TagEntity>> GetOrCreateByNamesAsync(Guid userId, IEnumerable<string> names, CancellationToken ct);
    Task<bool> HasTaggedTasksAsync(Guid tagId, CancellationToken ct);
}

public interface IReminderRepository
{
    Task<IReadOnlyList<ReminderEntity>> GetPendingAsync(DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<ReminderEntity>> GetForUserAsync(Guid userId, CancellationToken ct);
    Task<ReminderEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct);
    Task AddAsync(ReminderEntity reminder, CancellationToken ct);
    void Remove(ReminderEntity reminder);
    void MarkDelivered(ReminderEntity reminder);
}

public interface INotificationRepository
{
    Task<PagedResult<NotificationEntity>> GetPagedAsync(Guid userId, int page, int pageSize, CancellationToken ct);
    Task<NotificationEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<NotificationEntity>> GetUnreadAsync(Guid userId, CancellationToken ct);
    Task AddAsync(NotificationEntity notification, CancellationToken ct);
}

public interface IVoiceRecordingRepository
{
    Task AddAsync(VoiceRecordingEntity recording, CancellationToken ct);
    Task<VoiceRecordingEntity?> GetByIdAsync(Guid id, CancellationToken ct);
}

public interface IUserRepository
{
    Task<UserEntity?> GetByEmailAsync(string email, CancellationToken ct);
    Task<UserEntity?> GetByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(UserEntity user, CancellationToken ct);

    // --- admin console ---
    Task<PagedResult<UserEntity>> GetPagedAsync(AdminUserFilter filter, CancellationToken ct);
    Task<(int Total, int Active, int Deactivated, int NewSince)> GetStatsAsync(DateTime since, CancellationToken ct);
    Task<int> CountActiveAdminsAsync(CancellationToken ct);
    Task<bool> IsActiveAsync(Guid id, CancellationToken ct);
    Task<UserSecuritySnapshot?> GetSecuritySnapshotAsync(Guid id, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, Guid? excludeId, CancellationToken ct);
    Task<UserEntity?> GetByIdIncludingDeletedAsync(Guid id, CancellationToken ct);
    Task AddStatusChangeAsync(UserStatusChangeEntity change, CancellationToken ct);
}

public interface IPasswordResetRepository
{
    Task AddAsync(PasswordResetEntity reset, CancellationToken ct);
    /// <summary>Most recent still-usable (unused, unexpired) reset for the user.</summary>
    Task<PasswordResetEntity?> GetUsableForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct);
    /// <summary>Marks every not-yet-used reset for the user as used (supersede on a new request / on completion).</summary>
    Task InvalidateActiveForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct);
    /// <summary>How many resets the user has requested since <paramref name="since"/> (per-email rate limiting).</summary>
    Task<int> CountForUserSinceAsync(Guid userId, DateTime since, CancellationToken ct);
}
