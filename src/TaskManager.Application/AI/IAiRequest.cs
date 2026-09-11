namespace TaskManager.Application.AI;

/// <summary>
/// Marker interface for every MediatR command/query that invokes <see cref="IAIService"/>.
/// Picked up by <see cref="AiUsageLoggingBehavior{TRequest,TResponse}"/> to enforce the daily
/// quota before dispatch and record token usage after it.
/// </summary>
public interface IAiRequest
{
    /// <summary>Short label recorded in ai_usage.request_type, e.g. "analyze_task", "assistant".</summary>
    string AiRequestType { get; }
}
