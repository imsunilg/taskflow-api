using Microsoft.AspNetCore.Mvc;
using TaskManager.Application.Tasks;
using TaskManager.Application.Tasks.Commands;
using TaskManager.Application.Tasks.DTOs;
using TaskManager.Application.Tasks.Queries;

namespace TaskManager.Api.Controllers;

public class TasksController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetTasks([FromQuery] TaskQueryParameters query, CancellationToken ct)
        => FromPaged(await Mediator.Send(new GetTasksQuery(query), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTask(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new GetTaskByIdQuery(id), ct));

    [HttpPost]
    public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequest body, CancellationToken ct)
    {
        var result = await Mediator.Send(new CreateTaskCommand(body), ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetTask), new { id = result.Value!.Id },
                Application.Common.ApiResponse<TaskResponse>.FromResult(result))
            : BadRequest(Application.Common.ApiResponse<TaskResponse>.FromResult(result));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTask(Guid id, [FromBody] UpdateTaskRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new UpdateTaskCommand(id, body), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTask(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new DeleteTaskCommand(id), ct));

    [HttpPost("{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new CompleteTaskCommand(id), ct));

    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new ReopenTaskCommand(id), ct));

    [HttpPost("{id:guid}/subtasks")]
    public async Task<IActionResult> CreateSubtask(Guid id, [FromBody] CreateSubtaskRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new CreateSubtaskCommand(id, body), ct));

    [HttpPut("{id:guid}/subtasks/{subtaskId:guid}")]
    public async Task<IActionResult> UpdateSubtask(Guid id, Guid subtaskId, [FromBody] UpdateSubtaskRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new UpdateSubtaskCommand(id, subtaskId, body), ct));

    [HttpDelete("{id:guid}/subtasks/{subtaskId:guid}")]
    public async Task<IActionResult> DeleteSubtask(Guid id, Guid subtaskId, CancellationToken ct)
        => FromResult(await Mediator.Send(new DeleteSubtaskCommand(id, subtaskId), ct));
}
