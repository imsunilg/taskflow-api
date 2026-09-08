using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskManager.Application.Common;

namespace TaskManager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    private ISender? _mediator;
    protected ISender Mediator => _mediator ??= HttpContext.RequestServices.GetRequiredService<ISender>();

    protected IActionResult FromResult<T>(Result<T> result)
    {
        return result.IsSuccess
            ? Ok(ApiResponse<T>.FromResult(result))
            : BadRequest(ApiResponse<T>.FromResult(result));
    }

    protected IActionResult FromPaged<T>(Result<PagedResult<T>> result)
    {
        if (!result.IsSuccess)
        {
            return BadRequest(ApiResponse<PagedResult<T>>.FromResult(result));
        }
        return Ok(ApiResponse<PagedResult<T>>.FromPaged(result.Value!, result.Message));
    }
}
