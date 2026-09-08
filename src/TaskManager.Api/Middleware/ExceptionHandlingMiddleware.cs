using System.Text.Json;
using TaskManager.Application.Common;
using TaskManager.Domain.Common;
using ValidationException = TaskManager.Application.Common.ValidationException;

namespace TaskManager.Api.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception ex)
    {
        var correlationId = context.TraceIdentifier;
        var (status, message, errors) = ex switch
        {
            ValidationException v => (
                StatusCodes.Status400BadRequest,
                "Validation failed.",
                v.Errors.Select(e => new ApiErrorItem(e.Field, e.Message)).ToList()),
            NotFoundException n => (StatusCodes.Status404NotFound, n.Message, new List<ApiErrorItem>()),
            ForbiddenException f => (StatusCodes.Status403Forbidden, f.Message, new List<ApiErrorItem>()),
            DomainException d => (StatusCodes.Status400BadRequest, d.Message, new List<ApiErrorItem>()),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", new List<ApiErrorItem>()),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
            if (_env.IsDevelopment())
            {
                message = ex.Message;
            }
        }
        else
        {
            _logger.LogWarning("Handled {ExceptionType}: {Message}. CorrelationId={CorrelationId}",
                ex.GetType().Name, ex.Message, correlationId);
        }

        var response = new ApiResponse<object>
        {
            Success = false,
            Message = message,
            Errors = errors,
        };

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }
}
