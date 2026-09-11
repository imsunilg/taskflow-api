namespace TaskManager.Infrastructure.AI;

/// <summary>
/// Bound from configuration section "Gemini". <see cref="ApiKey"/> must never be committed —
/// set it via `dotnet user-secrets set "Gemini:ApiKey" "..."` locally, or the Gemini__ApiKey
/// environment variable in every other environment. appsettings*.json only ever carries
/// placeholders for the non-secret fields below.
/// </summary>
public class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-flash-latest";
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxOutputTokens { get; set; } = 2048;
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models/";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
