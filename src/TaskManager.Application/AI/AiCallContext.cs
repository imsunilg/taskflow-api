namespace TaskManager.Application.AI;

/// <summary>
/// Scoped ambient record of the most recent Gemini call made during the current request.
/// <see cref="IAIService"/> implementations set this after every model call; the usage-logging
/// pipeline behavior reads it once the handler completes and persists it to ai_usage. Kept as a
/// simple scoped POCO (not a MediatR pipeline concern) so IAIService stays a pure data-in/data-out
/// interface with no logging responsibilities of its own.
/// </summary>
public class AiCallContext
{
    public bool HasUsage { get; private set; }
    public string Model { get; private set; } = string.Empty;
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }

    public void Record(string model, int inputTokens, int outputTokens)
    {
        HasUsage = true;
        Model = model;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }
}
