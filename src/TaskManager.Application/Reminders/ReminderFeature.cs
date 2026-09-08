using FluentValidation;
using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Reminders;

public record ReminderResponse(Guid Id, Guid TaskId, int OffsetMinutes, DateTime FiresAt, DateTime? DeliveredAt, DateTime CreatedAt);
public record CreateReminderRequest(Guid TaskId, int OffsetMinutes, DateTime FiresAt);
public record UpdateReminderRequest(int? OffsetMinutes, DateTime? FiresAt);

public static class ReminderMapper
{
    public static ReminderResponse ToResponse(this ReminderEntity e) =>
        new(e.Id, e.TaskId, e.OffsetMinutes, e.FiresAt, e.DeliveredAt, e.CreatedAt);
}

public record GetRemindersQuery : IRequest<Result<List<ReminderResponse>>>;
public record CreateReminderCommand(CreateReminderRequest Request) : IRequest<Result<ReminderResponse>>;
public record UpdateReminderCommand(Guid Id, UpdateReminderRequest Request) : IRequest<Result<ReminderResponse>>;
public record DeleteReminderCommand(Guid Id) : IRequest<Result<bool>>;

public class CreateReminderCommandValidator : AbstractValidator<CreateReminderCommand>
{
    public CreateReminderCommandValidator()
    {
        RuleFor(x => x.Request.OffsetMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.FiresAt).GreaterThan(_ => DateTime.UtcNow)
            .WithMessage("Reminder must fire in the future.");
    }
}

public class ReminderHandlers :
    IRequestHandler<GetRemindersQuery, Result<List<ReminderResponse>>>,
    IRequestHandler<CreateReminderCommand, Result<ReminderResponse>>,
    IRequestHandler<UpdateReminderCommand, Result<ReminderResponse>>,
    IRequestHandler<DeleteReminderCommand, Result<bool>>
{
    private readonly IReminderRepository _repo;
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public ReminderHandlers(
        IReminderRepository repo,
        ITaskRepository tasks,
        ICurrentUserService currentUser,
        IUnitOfWork uow)
    {
        _repo = repo;
        _tasks = tasks;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<List<ReminderResponse>>> Handle(GetRemindersQuery q, CancellationToken ct)
    {
        var items = await _repo.GetForUserAsync(_currentUser.UserId, ct);
        return Result<List<ReminderResponse>>.Success(items.Select(r => r.ToResponse()).ToList());
    }

    public async Task<Result<ReminderResponse>> Handle(CreateReminderCommand c, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(_currentUser.UserId, c.Request.TaskId, ct)
                   ?? throw new NotFoundException("Task", c.Request.TaskId);

        var entity = new ReminderEntity
        {
            TaskId = task.Id,
            OffsetMinutes = c.Request.OffsetMinutes,
            FiresAt = c.Request.FiresAt,
        };
        await _repo.AddAsync(entity, ct);
        await _uow.CommitAsync(ct);
        return Result<ReminderResponse>.Success(entity.ToResponse(), "Reminder created");
    }

    public async Task<Result<ReminderResponse>> Handle(UpdateReminderCommand c, CancellationToken ct)
    {
        var entity = await _repo.GetByIdAsync(_currentUser.UserId, c.Id, ct)
                     ?? throw new NotFoundException("Reminder", c.Id);
        if (c.Request.OffsetMinutes.HasValue) entity.OffsetMinutes = c.Request.OffsetMinutes.Value;
        if (c.Request.FiresAt.HasValue) entity.FiresAt = c.Request.FiresAt.Value;
        await _uow.CommitAsync(ct);
        return Result<ReminderResponse>.Success(entity.ToResponse(), "Reminder updated");
    }

    public async Task<Result<bool>> Handle(DeleteReminderCommand c, CancellationToken ct)
    {
        var entity = await _repo.GetByIdAsync(_currentUser.UserId, c.Id, ct)
                     ?? throw new NotFoundException("Reminder", c.Id);
        _repo.Remove(entity);
        await _uow.CommitAsync(ct);
        return Result<bool>.Success(true, "Reminder deleted");
    }
}
