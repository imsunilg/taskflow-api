using FluentAssertions;
using MediatR;
using NSubstitute;
using TaskManager.Application.AI;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Tasks.Commands;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.UnitTests;

public class ToolDispatcherTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ISender _mediator = Substitute.For<ISender>();
    private readonly ITaskRepository _tasks = Substitute.For<ITaskRepository>();
    private readonly ICategoryRepository _categories = Substitute.For<ICategoryRepository>();
    private readonly ICurrentUserService _currentUser;
    private readonly IAIService _ai = Substitute.For<IAIService>();
    private readonly ToolDispatcher _dispatcher;

    public ToolDispatcherTests()
    {
        _currentUser = Substitute.For<ICurrentUserService>();
        _currentUser.UserId.Returns(_userId);
        _dispatcher = new ToolDispatcher(_mediator, _tasks, _categories, _currentUser, _ai);
    }

    private static TaskEntity MakeTask(string title, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), Title = title, Priority = Priority.Medium,
    };

    [Fact]
    public void Every_catalog_tool_name_is_recognised_and_nothing_else_is()
    {
        foreach (var name in AssistantToolCatalog.Names)
        {
            ToolDispatcher.IsKnownTool(name).Should().BeTrue();
        }
        ToolDispatcher.IsKnownTool("SomethingNotInTheCatalog").Should().BeFalse();
    }

    [Fact]
    public async Task BuildPreviewAsync_for_CreateTask_never_calls_the_mediator()
    {
        var args = new Dictionary<string, object?> { ["title"] = "Buy milk" };

        var result = await _dispatcher.BuildPreviewAsync("CreateTask", args, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Preview.Should().BeOfType<CreateTaskPreview>();
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(IRequest<Result<TaskResponse>>)!, default);
    }

    [Fact]
    public async Task ApplyAsync_for_CreateTask_dispatches_exactly_one_CreateTaskCommand()
    {
        _mediator.Send(Arg.Any<CreateTaskCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskResponse>.Success(new TaskResponse(
                Guid.NewGuid(), _userId, "Buy milk", null, null, null, "Medium", null, null, null,
                false, null, false, null, false, Array.Empty<string>(), Array.Empty<SubtaskResponse>(),
                null, null, Array.Empty<TaskHistoryResponse>(), DateTime.UtcNow, DateTime.UtcNow)));

        var result = await _dispatcher.ApplyAsync(
            "CreateTask", new Dictionary<string, object?> { ["title"] = "Buy milk" }, CancellationToken.None);

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(Arg.Any<CreateTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_for_an_unknown_task_reference_fails_without_calling_the_mediator()
    {
        _tasks.SearchAsync(_userId, Arg.Any<string>(), 1, 3, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<TaskEntity>(Array.Empty<TaskEntity>(), 1, 3, 0));

        var result = await _dispatcher.BuildPreviewAsync(
            "DeleteTask", new Dictionary<string, object?> { ["titleQuery"] = "nonexistent task" }, CancellationToken.None);

        result.Success.Should().BeFalse();
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(IRequest<Result<bool>>)!, default);
    }

    [Fact]
    public async Task Task_lookups_are_always_scoped_to_the_current_users_id()
    {
        _tasks.SearchAsync(Arg.Any<Guid>(), "my task", 1, 3, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<TaskEntity>(new[] { MakeTask("My Task") }, 1, 3, 1));

        await _dispatcher.BuildPreviewAsync(
            "CompleteTask", new Dictionary<string, object?> { ["titleQuery"] = "my task" }, CancellationToken.None);

        await _tasks.Received(1).SearchAsync(_userId, "my task", 1, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Calling_BuildPreviewAsync_on_a_read_only_tool_throws()
    {
        var act = () => _dispatcher.BuildPreviewAsync("GetTodayTasks", new Dictionary<string, object?>(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RescheduleBulk_preview_lists_every_overdue_task_without_writing_anything()
    {
        var overdue = new List<TaskEntity> { MakeTask("A"), MakeTask("B") };
        _tasks.GetOverdueAsync(_userId, Arg.Any<CancellationToken>()).Returns(overdue);

        var result = await _dispatcher.BuildPreviewAsync("RescheduleBulk", new Dictionary<string, object?>
        {
            ["scope"] = "overdue", ["newDueDate"] = "2027-01-01",
        }, CancellationToken.None);

        result.Success.Should().BeTrue();
        var preview = result.Preview.Should().BeOfType<RescheduleDiffPreview>().Subject;
        preview.Changes.Should().HaveCount(2);
        await _mediator.DidNotReceiveWithAnyArgs().Send(default(IRequest<Result<TaskResponse>>)!, default);
    }
}
