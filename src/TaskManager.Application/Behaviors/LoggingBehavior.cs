using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Interfaces;

namespace TaskManager.Application.Behaviors;

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly ICurrentUserService _currentUser;

    public LoggingBehavior(
        ILogger<LoggingBehavior<TRequest, TResponse>> logger,
        ICurrentUserService currentUser)
    {
        _logger = logger;
        _currentUser = currentUser;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await next();
            sw.Stop();
            _logger.LogInformation(
                "Handled {RequestName} in {ElapsedMs}ms for user {UserId}",
                name, sw.ElapsedMilliseconds, SafeUserId());
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(
                ex,
                "{RequestName} failed after {ElapsedMs}ms for user {UserId}",
                name, sw.ElapsedMilliseconds, SafeUserId());
            throw;
        }
    }

    private string SafeUserId()
    {
        try
        {
            return _currentUser.IsAuthenticated ? _currentUser.UserId.ToString() : "anonymous";
        }
        catch
        {
            return "anonymous";
        }
    }
}
