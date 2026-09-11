using FluentValidation;
using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.AI;

// ---- preview (no writes) ----------------------------------------------------

public record PreviewBreakdownCommand(Guid TaskId) : IRequest<Result<SubtaskTreeDto>>, IAiRequest
{
    public string AiRequestType => "breakdown_task";
}

public class PreviewBreakdownValidator : AbstractValidator<PreviewBreakdownCommand>
{
    public PreviewBreakdownValidator()
    {
        RuleFor(x => x.TaskId).NotEmpty();
    }
}

// ---- confirm: creates only the subtasks the user kept -----------------------

public record ConfirmBreakdownRequest(string[] SubtaskTitles);
public record ConfirmBreakdownCommand(Guid TaskId, ConfirmBreakdownRequest Request) : IRequest<Result<int>>;

public class ConfirmBreakdownValidator : AbstractValidator<ConfirmBreakdownCommand>
{
    public ConfirmBreakdownValidator()
    {
        RuleFor(x => x.TaskId).NotEmpty();
        RuleFor(x => x.Request.SubtaskTitles).NotNull();
        RuleForEach(x => x.Request.SubtaskTitles).NotEmpty().MaximumLength(500)
            .When(x => x.Request.SubtaskTitles is { Length: > 0 });
    }
}

public class BreakdownHandlers :
    IRequestHandler<PreviewBreakdownCommand, Result<SubtaskTreeDto>>,
    IRequestHandler<ConfirmBreakdownCommand, Result<int>>
{
    private const int MaxSubtasks = 12;

    private readonly IAIService _ai;
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public BreakdownHandlers(IAIService ai, ITaskRepository tasks, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _ai = ai;
        _tasks = tasks;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<SubtaskTreeDto>> Handle(PreviewBreakdownCommand command, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(_currentUser.UserId, command.TaskId, ct)
                   ?? throw new NotFoundException("Task", command.TaskId);

        var tree = await _ai.BreakDownTaskAsync(task.Title, task.Description, ct);

        // Defensive cap — enforced again here even though the prompt already asks for <= 12.
        if (tree.Subtasks.Count > MaxSubtasks)
        {
            tree = tree with { Subtasks = tree.Subtasks.Take(MaxSubtasks).ToList() };
        }
        return Result<SubtaskTreeDto>.Success(tree);
    }

    public async Task<Result<int>> Handle(ConfirmBreakdownCommand command, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(_currentUser.UserId, command.TaskId, ct)
                   ?? throw new NotFoundException("Task", command.TaskId);

        var titles = command.Request.SubtaskTitles
            .Select(t => t?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Take(MaxSubtasks)
            .ToList();

        if (titles.Count == 0)
        {
            return Result<int>.Success(0, "Nothing selected to add.");
        }

        // Single transaction: either every selected subtask is added, or none are.
        var startOrder = task.Subtasks.Count;
        var i = 0;
        foreach (var title in titles)
        {
            task.Subtasks.Add(new SubtaskEntity { Text = title!, SortOrder = startOrder + i++ });
        }
        task.History.Add(new TaskHistoryEntity
        {
            ChangeText = $"{titles.Count} subtask(s) added by AI",
            ChangedAt = DateTime.UtcNow,
            ChangedBy = _currentUser.UserId,
        });

        await _uow.CommitAsync(ct);
        return Result<int>.Success(titles.Count, $"{titles.Count} subtask(s) added.");
    }
}
