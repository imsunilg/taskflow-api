using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskManager.Application.Admin;

namespace TaskManager.Api.Controllers;

[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ApiControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? sort,
        [FromQuery] bool desc = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool includeDeleted = false,
        CancellationToken ct = default)
        => FromPaged(await Mediator.Send(
            new GetAdminUsersQuery(search, status, sort, desc, page, pageSize, includeDeleted), ct));

    [HttpGet("users/summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
        => FromResult(await Mediator.Send(new GetAdminUserSummaryQuery(), ct));

    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new GetAdminUserQuery(id), ct));

    [HttpPost("users")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new CreateUserCommand(body), ct));

    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new UpdateUserCommand(id, body), ct));

    [HttpPost("users/{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new ResetUserPasswordCommand(id, body), ct));

    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new DeleteUserCommand(id), ct));

    public record UpdateStatusBody(bool IsActive, string? Reason);

    [HttpPatch("users/{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] UpdateStatusBody body, CancellationToken ct)
        => FromResult(await Mediator.Send(new SetUserStatusCommand(id, body.IsActive, body.Reason), ct));
}
