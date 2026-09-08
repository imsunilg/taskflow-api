using System.Text.Json;
using TaskManager.Application.Common;

namespace TaskManager.Api.Middleware;

/// <summary>
/// When the authenticated principal carries the <c>mcp</c> claim (set after a one-time-password
/// reset login), every protected endpoint is blocked with 403 <c>must_change_password</c> except
/// the handful needed to finish the forced password change.
/// </summary>
public class MustChangePasswordMiddleware
{
    private static readonly string[] Allowlist =
    {
        "/api/auth/complete-password-reset",
        "/api/auth/logout",
        "/api/auth/refresh",
    };

    private readonly RequestDelegate _next;

    public MustChangePasswordMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true && user.HasClaim("mcp", "1"))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var allowed = Allowlist.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                var body = ApiResponse<object>.Fail(
                    "You must set a new password before continuing.",
                    new[] { new ApiErrorItem("code", "must_change_password") });
                await context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                }));
                return;
            }
        }

        await _next(context);
    }
}
