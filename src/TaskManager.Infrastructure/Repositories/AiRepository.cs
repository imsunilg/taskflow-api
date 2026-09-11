using Microsoft.EntityFrameworkCore;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Infrastructure.Persistence;

namespace TaskManager.Infrastructure.Repositories;

public class AiRepository : IAiRepository
{
    private readonly TaskManagerDbContext _db;
    public AiRepository(TaskManagerDbContext db) => _db = db;

    public async Task<AiConversationEntity> GetOrCreateConversationAsync(Guid userId, CancellationToken ct)
    {
        var existing = await _db.AiConversations
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new AiConversationEntity { UserId = userId, Title = "AI Assistant" };
        await _db.AiConversations.AddAsync(created, ct);
        return created;
    }

    public async Task<IReadOnlyList<AiMessageEntity>> GetRecentMessagesAsync(Guid conversationId, int take, CancellationToken ct) =>
        await _db.AiMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    public async Task AddMessageAsync(AiMessageEntity message, CancellationToken ct) =>
        await _db.AiMessages.AddAsync(message, ct);

    public async Task AddUsageAsync(AiUsageEntity usage, CancellationToken ct) =>
        await _db.AiUsage.AddAsync(usage, ct);

    public Task<int> CountUsageSinceAsync(Guid userId, DateTime sinceUtc, CancellationToken ct) =>
        _db.AiUsage.CountAsync(u => u.UserId == userId && u.CreatedAt >= sinceUtc, ct);

    public async Task UpsertDailyPlanAsync(AiDailyPlanEntity plan, CancellationToken ct)
    {
        var existing = await _db.AiDailyPlans
            .FirstOrDefaultAsync(p => p.UserId == plan.UserId && p.PlanDate == plan.PlanDate, ct);
        if (existing is null)
        {
            await _db.AiDailyPlans.AddAsync(plan, ct);
        }
        else
        {
            existing.Summary = plan.Summary;
            existing.BlocksJson = plan.BlocksJson;
        }
    }

    public Task<AiDailyPlanEntity?> GetDailyPlanAsync(Guid userId, DateOnly date, CancellationToken ct) =>
        _db.AiDailyPlans.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.PlanDate == date, ct);
}
