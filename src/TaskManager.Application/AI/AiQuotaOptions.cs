namespace TaskManager.Application.AI;

/// <summary>Bound from the same "Gemini" config section as the Infrastructure-side GeminiOptions.</summary>
public class AiQuotaOptions
{
    /// <summary>Max AI requests (of any kind) a single user may make per rolling 24h. 0 disables the cap.</summary>
    public int DailyRequestCap { get; set; } = 100;
}
