using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.AI;

/// <summary>
/// Wraps every MediatR command/query marked <see cref="IAiRequest"/>: enforces the per-user daily
/// AI quota before dispatch (short-circuits with <see cref="AIQuotaExceededException"/> → HTTP 429
/// once exceeded), then records the model/token usage <see cref="AiCallContext"/> picked up from the
/// handler's Gemini call, if any, to ai_usage.
/// </summary>
public class AiUsageLoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ICurrentUserService _currentUser;
    private readonly IAiRepository _aiRepo;
    private readonly IUnitOfWork _uow;
    private readonly AiCallContext _callContext;
    private readonly AiQuotaOptions _quota;
    private readonly ILogger<AiUsageLoggingBehavior<TRequest, TResponse>> _logger;

    public AiUsageLoggingBehavior(
        ICurrentUserService currentUser, IAiRepository aiRepo, IUnitOfWork uow,
        AiCallContext callContext, IOptions<AiQuotaOptions> quota,
        ILogger<AiUsageLoggingBehavior<TRequest, TResponse>> logger)
    {
        _currentUser = currentUser;
        _aiRepo = aiRepo;
        _uow = uow;
        _callContext = callContext;
        _quota = quota.Value;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is not IAiRequest aiRequest)
        {
            return await next();
        }

        var userId = _currentUser.UserId;

        if (_quota.DailyRequestCap > 0)
        {
            var since = DateTime.UtcNow.AddHours(-24);
            var usedToday = await _aiRepo.CountUsageSinceAsync(userId, since, ct);
            if (usedToday >= _quota.DailyRequestCap)
            {
                _logger.LogWarning(
                    "AI daily quota exceeded for user {UserId}: {Used}/{Cap}", userId, usedToday, _quota.DailyRequestCap);
                throw new AIQuotaExceededException(
                    $"You've reached today's AI request limit ({_quota.DailyRequestCap}). Please try again tomorrow.");
            }
        }

        var response = await next();

        if (_callContext.HasUsage)
        {
            await _aiRepo.AddUsageAsync(new AiUsageEntity
            {
                UserId = userId,
                Model = _callContext.Model,
                RequestType = aiRequest.AiRequestType,
                InputTokens = _callContext.InputTokens,
                OutputTokens = _callContext.OutputTokens,
            }, ct);
            await _uow.CommitAsync(ct);
        }

        return response;
    }
}
