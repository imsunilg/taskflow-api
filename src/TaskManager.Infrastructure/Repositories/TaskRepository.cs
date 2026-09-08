using Microsoft.EntityFrameworkCore;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;
using TaskManager.Infrastructure.Persistence;

namespace TaskManager.Infrastructure.Repositories;

public class TaskRepository : ITaskRepository
{
    private readonly TaskManagerDbContext _db;

    public TaskRepository(TaskManagerDbContext db) => _db = db;

    // Compiled queries for the hottest list paths (today / upcoming).
    private static readonly Func<TaskManagerDbContext, Guid, DateOnly, IAsyncEnumerable<TaskEntity>> _todayQuery =
        EF.CompileAsyncQuery((TaskManagerDbContext db, Guid userId, DateOnly today) =>
            db.Tasks
                .Include(t => t.Category)
                .Include(t => t.Reminder)
                .Include(t => t.RecurrenceRule)
                .Include(t => t.Subtasks)
                .Include(t => t.TaskTags).ThenInclude(tt => tt.Tag)
                .Include(t => t.History)
                .AsNoTracking()
                .Where(t => t.UserId == userId && !t.Completed && t.DueDate == today));

    private static readonly Func<TaskManagerDbContext, Guid, DateOnly, IAsyncEnumerable<TaskEntity>> _upcomingQuery =
        EF.CompileAsyncQuery((TaskManagerDbContext db, Guid userId, DateOnly today) =>
            db.Tasks
                .Include(t => t.Category)
                .Include(t => t.Reminder)
                .Include(t => t.RecurrenceRule)
                .Include(t => t.Subtasks)
                .Include(t => t.TaskTags).ThenInclude(tt => tt.Tag)
                .Include(t => t.History)
                .AsNoTracking()
                .Where(t => t.UserId == userId && !t.Completed && t.DueDate > today));

    private IQueryable<TaskEntity> FullGraph() =>
        _db.Tasks
            .Include(t => t.Category)
            .Include(t => t.Reminder)
            .Include(t => t.RecurrenceRule)
            .Include(t => t.Subtasks)
            .Include(t => t.TaskTags).ThenInclude(tt => tt.Tag)
            .Include(t => t.History);

    public async Task<PagedResult<TaskEntity>> GetPagedAsync(Guid userId, TaskQueryParameters q, CancellationToken ct)
    {
        var query = FullGraph().AsNoTracking().Where(t => t.UserId == userId);
        query = ApplyFilters(query, q);
        query = ApplySort(query, q.Sort);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TaskEntity>(items, q.Page, q.PageSize, total);
    }

    private static IQueryable<TaskEntity> ApplyFilters(IQueryable<TaskEntity> query, TaskQueryParameters q)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            query = q.Status.ToLowerInvariant() switch
            {
                "active" => query.Where(t => !t.Completed),
                "completed" => query.Where(t => t.Completed),
                "overdue" => query.Where(t => !t.Completed && t.DueDate != null && t.DueDate < today),
                _ => query,
            };
        }

        if (!string.IsNullOrWhiteSpace(q.Priority)
            && Enum.TryParse<Priority>(q.Priority, true, out var priority))
        {
            query = query.Where(t => t.Priority == priority);
        }

        if (q.CategoryId is { } catId)
        {
            query = query.Where(t => t.CategoryId == catId);
        }

        if (!string.IsNullOrWhiteSpace(q.TagIds))
        {
            var tagIds = q.TagIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToArray();
            if (tagIds.Length > 0)
            {
                query = query.Where(t => t.TaskTags.Any(tt => tagIds.Contains(tt.TagId)));
            }
        }

        if (!string.IsNullOrWhiteSpace(q.DueDate))
        {
            query = q.DueDate.ToLowerInvariant() switch
            {
                "today" => query.Where(t => t.DueDate == today),
                "no-date" => query.Where(t => t.DueDate == null),
                "this-week" => query.Where(t => t.DueDate != null
                    && t.DueDate >= today && t.DueDate <= today.AddDays(7)),
                _ => query,
            };
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = $"%{q.Search.Trim()}%";
            query = query.Where(t =>
                EF.Functions.ILike(t.Title, term) ||
                (t.Description != null && EF.Functions.ILike(t.Description, term)) ||
                (t.Notes != null && EF.Functions.ILike(t.Notes, term)));
        }

        return query;
    }

    private static IQueryable<TaskEntity> ApplySort(IQueryable<TaskEntity> query, string? sort) => sort switch
    {
        "priority" => query.OrderBy(t => t.Priority),
        "created" => query.OrderByDescending(t => t.CreatedAt),
        "updated" => query.OrderByDescending(t => t.UpdatedAt),
        "alpha" => query.OrderBy(t => t.Title),
        _ => query.OrderBy(t => t.DueDate ?? DateOnly.MaxValue).ThenBy(t => t.DueTime),
    };

    public Task<TaskEntity?> GetByIdAsync(Guid userId, Guid id, CancellationToken ct) =>
        FullGraph().FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);

    public async Task AddAsync(TaskEntity task, CancellationToken ct) =>
        await _db.Tasks.AddAsync(task, ct);

    private Task<List<TaskEntity>> ActiveByUser(Guid userId) =>
        FullGraph().AsNoTracking().Where(t => t.UserId == userId && !t.Completed).ToListAsync();

    public async Task<IReadOnlyList<TaskEntity>> GetTodayAsync(Guid userId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var list = new List<TaskEntity>();
        await foreach (var t in _todayQuery(_db, userId, today).WithCancellation(ct))
        {
            list.Add(t);
        }
        return list;
    }

    public async Task<IReadOnlyList<TaskEntity>> GetUpcomingAsync(Guid userId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var list = new List<TaskEntity>();
        await foreach (var t in _upcomingQuery(_db, userId, today).WithCancellation(ct))
        {
            list.Add(t);
        }
        return list;
    }

    public async Task<IReadOnlyList<TaskEntity>> GetOverdueAsync(Guid userId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await FullGraph().AsNoTracking()
            .Where(t => t.UserId == userId && !t.Completed && t.DueDate != null && t.DueDate < today)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TaskEntity>> GetImportantAsync(Guid userId, CancellationToken ct) =>
        await FullGraph().AsNoTracking()
            .Where(t => t.UserId == userId && !t.Completed && t.Important).ToListAsync(ct);

    public async Task<IReadOnlyList<TaskEntity>> GetInboxAsync(Guid userId, CancellationToken ct) =>
        await FullGraph().AsNoTracking()
            .Where(t => t.UserId == userId && !t.Completed && t.DueDate == null && t.CategoryId == null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskEntity>> GetCalendarAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await FullGraph().AsNoTracking()
            .Where(t => t.UserId == userId && t.DueDate != null && t.DueDate >= from && t.DueDate <= to)
            .OrderBy(t => t.DueDate).ThenBy(t => t.DueTime)
            .ToListAsync(ct);

    public async Task<PagedResult<TaskEntity>> SearchAsync(Guid userId, string term, int page, int pageSize, CancellationToken ct)
    {
        var like = $"%{term}%";
        var query = FullGraph().AsNoTracking().Where(t =>
            t.UserId == userId && (
                EF.Functions.ILike(t.Title, like) ||
                (t.Description != null && EF.Functions.ILike(t.Description, like)) ||
                (t.Notes != null && EF.Functions.ILike(t.Notes, like)) ||
                t.TaskTags.Any(tt => tt.Tag != null && EF.Functions.ILike(tt.Tag.Name, like))));

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(t => t.UpdatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<TaskEntity>(items, page, pageSize, total);
    }
}
