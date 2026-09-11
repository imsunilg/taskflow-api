using FluentValidation;
using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;

namespace TaskManager.Application.AI;

// ---- Smart Quick Add: free text -> task draft preview (creates nothing) ----

public record AnalyzeTaskTextCommand(string Text) : IRequest<Result<TaskDraftDto>>, IAiRequest
{
    public string AiRequestType => "analyze_quick_add";
}

public class AnalyzeTaskTextValidator : AbstractValidator<AnalyzeTaskTextCommand>
{
    public AnalyzeTaskTextValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(500);
    }
}

// ---- Task Analyzer: read-only analysis of an existing task -----------------

public record GetTaskAnalysisQuery(Guid TaskId) : IRequest<Result<TaskAnalysisDto>>, IAiRequest
{
    public string AiRequestType => "analyze_task";
}

public class AnalyzeHandlers :
    IRequestHandler<AnalyzeTaskTextCommand, Result<TaskDraftDto>>,
    IRequestHandler<GetTaskAnalysisQuery, Result<TaskAnalysisDto>>
{
    private readonly IAIService _ai;
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;

    public AnalyzeHandlers(IAIService ai, ITaskRepository tasks, ICurrentUserService currentUser)
    {
        _ai = ai;
        _tasks = tasks;
        _currentUser = currentUser;
    }

    public async Task<Result<TaskDraftDto>> Handle(AnalyzeTaskTextCommand command, CancellationToken ct)
    {
        var draft = await _ai.AnalyzeTaskTextAsync(command.Text, DateTime.UtcNow, ct);
        return Result<TaskDraftDto>.Success(draft);
    }

    public async Task<Result<TaskAnalysisDto>> Handle(GetTaskAnalysisQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(_currentUser.UserId, query.TaskId, ct)
                   ?? throw new NotFoundException("Task", query.TaskId);

        var overdue = !task.Completed && task.DueDate is { } d && d < DateOnly.FromDateTime(DateTime.UtcNow);
        var nextOpenSubtask = task.Subtasks.Where(s => !s.IsDone).OrderBy(s => s.SortOrder).FirstOrDefault()?.Text;
        var tags = task.TaskTags.Select(tt => tt.Tag?.Name).Where(n => n is not null).Select(n => n!).ToArray();

        var analysis = await _ai.AnalyzeTaskAsync(
            task.Title, task.Description, task.Priority.ToString(), task.DueDate, task.Completed, overdue,
            task.Subtasks.Count, task.Subtasks.Count(s => s.IsDone), nextOpenSubtask, tags, ct);

        return Result<TaskAnalysisDto>.Success(analysis);
    }
}
