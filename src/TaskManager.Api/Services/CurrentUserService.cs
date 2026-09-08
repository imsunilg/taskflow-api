using System.Security.Claims;
using TaskManager.Application.Interfaces;

namespace TaskManager.Api.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUserService(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid UserId
    {
        get
        {
            var value = _accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? _accessor.HttpContext?.User.FindFirstValue("sub");
            return Guid.TryParse(value, out var id) ? id : Guid.Empty;
        }
    }

    public bool IsAuthenticated => _accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;

    public bool IsAdmin => _accessor.HttpContext?.User.IsInRole("Admin") ?? false;
}
