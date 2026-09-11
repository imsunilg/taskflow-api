using Microsoft.EntityFrameworkCore;
using TaskManager.Application.Admin;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;
using TaskManager.Infrastructure.Persistence;

namespace TaskManager.Infrastructure.Repositories;

public class CategoryRepository : ICategoryRepository
{
    private readonly TaskManagerDbContext _db;
    public CategoryRepository(TaskManagerDbContext db) => _db = db;

    public async Task<IReadOnlyList<CategoryEntity>> GetAllAsync(Guid userId, CancellationToken ct) =>
        await _db.Categories.AsNoTracking().Where(c => c.UserId == userId).OrderBy(c => c.Name).ToListAsync(ct);

    public Task<CategoryEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct) =>
        _db.Categories.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);

    public async Task AddAsync(CategoryEntity category, CancellationToken ct) => await _db.Categories.AddAsync(category, ct);

    public void Remove(CategoryEntity category) => _db.Categories.Remove(category);

    public Task<bool> NameExistsAsync(Guid userId, string name, Guid? excludeId, CancellationToken ct) =>
        _db.Categories.AnyAsync(c => c.UserId == userId
            && c.Name.ToLower() == name.Trim().ToLower()
            && (excludeId == null || c.Id != excludeId), ct);
}

public class TagRepository : ITagRepository
{
    private readonly TaskManagerDbContext _db;
    public TagRepository(TaskManagerDbContext db) => _db = db;

    public async Task<IReadOnlyList<TagEntity>> GetAllAsync(Guid userId, CancellationToken ct) =>
        await _db.Tags.AsNoTracking().Where(t => t.UserId == userId).OrderBy(t => t.Name).ToListAsync(ct);

    public Task<TagEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct) =>
        _db.Tags.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);

    public async Task AddAsync(TagEntity tag, CancellationToken ct) => await _db.Tags.AddAsync(tag, ct);

    public void Remove(TagEntity tag) => _db.Tags.Remove(tag);

    public async Task<IReadOnlyList<TagEntity>> GetOrCreateByNamesAsync(Guid userId, IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return Array.Empty<TagEntity>();

        var existing = await _db.Tags.Where(t => t.UserId == userId).ToListAsync(ct);
        var result = new List<TagEntity>();
        foreach (var name in wanted)
        {
            var match = existing.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = new TagEntity { UserId = userId, Name = name };
                await _db.Tags.AddAsync(match, ct);
                existing.Add(match);
            }
            result.Add(match);
        }
        return result;
    }

    public Task<bool> HasTaggedTasksAsync(Guid tagId, CancellationToken ct) =>
        _db.TaskTags.AnyAsync(tt => tt.TagId == tagId, ct);
}

public class ReminderRepository : IReminderRepository
{
    private readonly TaskManagerDbContext _db;
    public ReminderRepository(TaskManagerDbContext db) => _db = db;

    public async Task<IReadOnlyList<ReminderEntity>> GetPendingAsync(DateTime nowUtc, CancellationToken ct) =>
        await _db.Reminders.Include(r => r.Task)
            .Where(r => r.DeliveredAt == null && r.FiresAt <= nowUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ReminderEntity>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await _db.Reminders.AsNoTracking()
            .Where(r => r.Task != null && r.Task.UserId == userId)
            .OrderBy(r => r.FiresAt).ToListAsync(ct);

    public Task<ReminderEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct) =>
        _db.Reminders.Include(r => r.Task)
            .FirstOrDefaultAsync(r => r.Id == id && r.Task != null && r.Task.UserId == userId, ct);

    public async Task AddAsync(ReminderEntity reminder, CancellationToken ct) => await _db.Reminders.AddAsync(reminder, ct);

    public void Remove(ReminderEntity reminder) => _db.Reminders.Remove(reminder);

    public void MarkDelivered(ReminderEntity reminder) => reminder.DeliveredAt = DateTime.UtcNow;
}

public class NotificationRepository : INotificationRepository
{
    private readonly TaskManagerDbContext _db;
    public NotificationRepository(TaskManagerDbContext db) => _db = db;

    public async Task<PagedResult<NotificationEntity>> GetPagedAsync(Guid userId, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.CreatedAt);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<NotificationEntity>(items, page, pageSize, total);
    }

    public Task<NotificationEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct) =>
        _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

    public async Task<IReadOnlyList<NotificationEntity>> GetUnreadAsync(Guid userId, CancellationToken ct) =>
        await _db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(ct);

    public async Task AddAsync(NotificationEntity notification, CancellationToken ct) =>
        await _db.Notifications.AddAsync(notification, ct);
}

public class VoiceRecordingRepository : IVoiceRecordingRepository
{
    private readonly TaskManagerDbContext _db;
    public VoiceRecordingRepository(TaskManagerDbContext db) => _db = db;

    public async Task AddAsync(VoiceRecordingEntity recording, CancellationToken ct) =>
        await _db.VoiceRecordings.AddAsync(recording, ct);

    public Task<VoiceRecordingEntity?> GetByIdAsync(Guid id, CancellationToken ct) =>
        _db.VoiceRecordings.Include(v => v.Transcription).FirstOrDefaultAsync(v => v.Id == id, ct);
}

public class PasswordResetRepository : IPasswordResetRepository
{
    private readonly TaskManagerDbContext _db;
    public PasswordResetRepository(TaskManagerDbContext db) => _db = db;

    public async Task AddAsync(PasswordResetEntity reset, CancellationToken ct) =>
        await _db.PasswordResets.AddAsync(reset, ct);

    public Task<PasswordResetEntity?> GetUsableForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct) =>
        _db.PasswordResets
            .Where(r => r.UserId == userId && r.UsedAt == null && r.ExpiresAt > nowUtc)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task InvalidateActiveForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        var active = await _db.PasswordResets
            .Where(r => r.UserId == userId && r.UsedAt == null)
            .ToListAsync(ct);
        foreach (var reset in active)
        {
            reset.UsedAt = nowUtc;
        }
    }

    public Task<int> CountForUserSinceAsync(Guid userId, DateTime since, CancellationToken ct) =>
        _db.PasswordResets.CountAsync(r => r.UserId == userId && r.CreatedAt >= since, ct);
}

public class UserRepository : IUserRepository
{
    private readonly TaskManagerDbContext _db;
    public UserRepository(TaskManagerDbContext db) => _db = db;

    public Task<UserEntity?> GetByEmailAsync(string email, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email.Trim().ToLower(), ct);

    public Task<UserEntity?> GetByIdAsync(Guid id, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task AddAsync(UserEntity user, CancellationToken ct) => await _db.Users.AddAsync(user, ct);

    public async Task<PagedResult<UserEntity>> GetPagedAsync(AdminUserFilter filter, CancellationToken ct)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();
        if (filter.IncludeDeleted) query = query.IgnoreQueryFilters();

        if (filter.Status == "active") query = query.Where(u => u.IsActive);
        else if (filter.Status == "deactivated") query = query.Where(u => !u.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(u =>
                u.Email.ToLower().Contains(term) ||
                u.DisplayName.ToLower().Contains(term) ||
                u.Id.ToString().ToLower().Contains(term));
        }

        query = (filter.Sort, filter.Desc) switch
        {
            ("displayName", false) => query.OrderBy(u => u.DisplayName),
            ("displayName", true) => query.OrderByDescending(u => u.DisplayName),
            ("email", false) => query.OrderBy(u => u.Email),
            ("email", true) => query.OrderByDescending(u => u.Email),
            ("role", false) => query.OrderBy(u => u.Role),
            ("role", true) => query.OrderByDescending(u => u.Role),
            ("status", false) => query.OrderBy(u => u.IsActive),
            ("status", true) => query.OrderByDescending(u => u.IsActive),
            ("lastLoginAt", false) => query.OrderBy(u => u.LastLoginAt),
            ("lastLoginAt", true) => query.OrderByDescending(u => u.LastLoginAt),
            (_, false) => query.OrderBy(u => u.CreatedAt),
            _ => query.OrderByDescending(u => u.CreatedAt),
        };

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);
        return new PagedResult<UserEntity>(items, filter.Page, filter.PageSize, total);
    }

    public async Task<(int Total, int Active, int Deactivated, int NewSince)> GetStatsAsync(DateTime since, CancellationToken ct)
    {
        var total = await _db.Users.CountAsync(ct);
        var active = await _db.Users.CountAsync(u => u.IsActive, ct);
        var newSince = await _db.Users.CountAsync(u => u.CreatedAt >= since, ct);
        return (total, active, total - active, newSince);
    }

    public Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        _db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct);

    public Task<bool> IsActiveAsync(Guid id, CancellationToken ct) =>
        _db.Users.AnyAsync(u => u.Id == id && u.IsActive, ct);

    public Task<UserSecuritySnapshot?> GetSecuritySnapshotAsync(Guid id, CancellationToken ct) =>
        _db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new UserSecuritySnapshot(u.IsActive, u.SecurityStamp))
            .FirstOrDefaultAsync(ct);

    public Task<bool> EmailExistsAsync(string email, Guid? excludeId, CancellationToken ct)
    {
        var term = email.Trim().ToLower();
        return _db.Users.AnyAsync(u => u.Email.ToLower() == term && (excludeId == null || u.Id != excludeId), ct);
    }

    public Task<UserEntity?> GetByIdIncludingDeletedAsync(Guid id, CancellationToken ct) =>
        _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task AddStatusChangeAsync(UserStatusChangeEntity change, CancellationToken ct) =>
        await _db.UserStatusChanges.AddAsync(change, ct);

    public async Task<IReadOnlyList<Guid>> GetActiveUserIdsAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync(ct);
}
