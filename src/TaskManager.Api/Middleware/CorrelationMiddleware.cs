using Serilog.Context;
using TaskManager.Application.Interfaces;

namespace TaskManager.Api.Middleware;

public class CorrelationMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICurrentUserService currentUser)
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                            ?? context.TraceIdentifier;
        context.Response.Headers["X-Correlation-ID"] = correlationId;

        var userId = currentUser.IsAuthenticated ? currentUser.UserId.ToString() : "anonymous";

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("UserId", userId))
        {
            await _next(context);
        }
    }
}
