using FluentAssertions;
using NSubstitute;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.Commands;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;
using Xunit;

namespace TaskManager.UnitTests;

public class TaskCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly ITagRepository _tags = Substitute.For<ITagRepository>();
    private readonly ICurrentUserService _currentUser = Substitute.For<ICurrentUserService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IRecurrenceService _recurrence = Substitute.For<IRecurrenceService>();
    private readonly ILookupCache _cache = Substitute.For<ILookupCache>();

    public TaskCommandHandlerTests()
    {
        _currentUser.UserId.Returns(UserId);
        _currentUser.IsAuthenticated.Returns(true);
        _tags.GetOrCreateByNamesAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TagEntity>());
    }

    private TaskEntity Owned(Action<TaskEntity>? mutate = null)
    {
        var t = new TaskEntity { Id = Guid.NewGuid(), UserId = UserId, Title = "Existing" };
        mutate?.Invoke(t);
        return t;
    }

    [Fact]
    public async Task Create_persists_task_and_returns_response()
    {
        _tasks.GetByIdAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Owned(x => x.Id = ci.ArgAt<Guid>(1)));

        var handler = new CreateTaskCommandHandler(_tasks, _tags, _currentUser, _uow, _cache);
        var result = await handler.Handle(
            new CreateTaskCommand(new CreateTaskRequest("New task", null, null, null,
                Priority.High, null, null, null, null, null, null, null)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _tasks.Received(1).AddAsync(Arg.Is<TaskEntity>(t => t.Title == "New task"), Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_on_unknown_task_throws_NotFound()
    {
        _tasks.GetByIdAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TaskEntity?)null);

        var handler = new UpdateTaskCommandHandler(_tasks, _tags, _currentUser, _uow);

        var act = () => handler.Handle(
            new UpdateTaskCommand(Guid.NewGuid(), new UpdateTaskRequest(
                "x", null, null, null, null, null, null, null, null, null, null, null, null)),
            CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Complete_marks_task_and_writes_history()
    {
        var task = Owned();
        _tasks.GetByIdAsync(UserId, task.Id, Arg.Any<CancellationToken>()).Returns(task);

        var handler = new CompleteTaskCommandHandler(_tasks, _currentUser, _recurrence, _uow);
        var result = await handler.Handle(new CompleteTaskCommand(task.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        task.Completed.Should().BeTrue();
        task.History.Should().Contain(h => h.ChangeText == "Marked complete");
    }

    [Fact]
    public async Task Complete_unknown_task_throws_NotFound()
    {
        _tasks.GetByIdAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TaskEntity?)null);
        var handler = new CompleteTaskCommandHandler(_tasks, _currentUser, _recurrence, _uow);

        await FluentActions.Awaiting(() => handler.Handle(new CompleteTaskCommand(Guid.NewGuid()), CancellationToken.None))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Reopen_clears_completion()
    {
        var task = Owned(t => { t.Completed = true; t.CompletedAt = DateTime.UtcNow; });
        _tasks.GetByIdAsync(UserId, task.Id, Arg.Any<CancellationToken>()).Returns(task);

        var handler = new ReopenTaskCommandHandler(_tasks, _currentUser, _uow);
        await handler.Handle(new ReopenTaskCommand(task.Id), CancellationToken.None);

        task.Completed.Should().BeFalse();
        task.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Delete_soft_deletes_and_writes_history()
    {
        var task = Owned();
        _tasks.GetByIdAsync(UserId, task.Id, Arg.Any<CancellationToken>()).Returns(task);

        var handler = new DeleteTaskCommandHandler(_tasks, _currentUser, _uow);
        await handler.Handle(new DeleteTaskCommand(task.Id), CancellationToken.None);

        task.IsDeleted.Should().BeTrue();
        task.History.Should().Contain(h => h.ChangeText == "Deleted");
    }

    [Fact]
    public async Task Complete_recurring_task_spawns_next_occurrence()
    {
        var due = new DateOnly(2026, 1, 1);
        var task = Owned(t =>
        {
            t.DueDate = due;
            t.RecurrenceRuleId = Guid.NewGuid();
            t.RecurrenceRule = new RecurrenceRuleEntity { Frequency = RecurrenceFrequency.Weekly, Interval = 1 };
        });
        _tasks.GetByIdAsync(UserId, task.Id, Arg.Any<CancellationToken>()).Returns(task);
        _recurrence.ComputeNextDueDate(Arg.Any<RecurrenceRuleEntity>(), due).Returns(due.AddDays(7));

        var handler = new CompleteTaskCommandHandler(_tasks, _currentUser, _recurrence, _uow);
        await handler.Handle(new CompleteTaskCommand(task.Id), CancellationToken.None);

        await _tasks.Received(1).AddAsync(
            Arg.Is<TaskEntity>(t => t.DueDate == due.AddDays(7) && !t.Completed),
            Arg.Any<CancellationToken>());
    }
}
