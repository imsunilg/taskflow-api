using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskManager.Application.AI;

namespace TaskManager.Infrastructure.AI;

public sealed record GeminiToolDeclaration(string Name, string Description, JsonObject ParametersSchema);

public sealed record GeminiTextResult(string RawText, int InputTokens, int OutputTokens);

public sealed record GeminiToolResult(string? ToolName, JsonObject? Arguments, string? TextReply, int InputTokens, int OutputTokens);

/// <summary>
/// Thin, typed wrapper around the Gemini `generateContent` REST endpoint. Knows nothing about
/// TaskFlow's domain — it only ever sends prompt text and returns raw JSON text / a raw tool
/// call. All prompt construction and response *interpretation* lives in <see cref="GeminiAIService"/>.
/// </summary>
public class GeminiClient
{
    private readonly HttpClient _http;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(HttpClient http, IOptions<GeminiOptions> options, ILogger<GeminiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds));
    }

    public bool IsConfigured => _options.IsConfigured;
    public string Model => _options.Model;

    /// <summary>Structured JSON output — used by every AI feature except the Assistant.</summary>
    public async Task<GeminiTextResult> GenerateJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = userPrompt }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["temperature"] = 0.2,
                ["maxOutputTokens"] = _options.MaxOutputTokens,
            },
        };

        var (candidateJson, usage) = await CallAsync(body, ct);
        var text = ExtractText(candidateJson)
            ?? throw new AIServiceException("Gemini returned no text content.");
        return new GeminiTextResult(text, usage.input, usage.output);
    }

    /// <summary>Free-form text output (no JSON mode) — used only for the daily summary.</summary>
    public async Task<GeminiTextResult> GenerateTextAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = userPrompt }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.4,
                ["maxOutputTokens"] = Math.Min(_options.MaxOutputTokens, 512),
            },
        };

        var (candidateJson, usage) = await CallAsync(body, ct);
        var text = ExtractText(candidateJson) ?? throw new AIServiceException("Gemini returned no text content.");
        return new GeminiTextResult(text, usage.input, usage.output);
    }

    /// <summary>Function-calling entry point used only by the AI Assistant.</summary>
    public async Task<GeminiToolResult> GenerateWithToolsAsync(
        string systemPrompt, IEnumerable<(string role, string text)> history, string userMessage,
        IReadOnlyList<GeminiToolDeclaration> tools, CancellationToken ct)
    {
        var contents = new JsonArray();
        foreach (var (role, turnText) in history)
        {
            contents.Add(new JsonObject
            {
                ["role"] = role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = turnText }),
            });
        }
        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = userMessage }) });

        var functionDeclarations = new JsonArray();
        foreach (var tool in tools)
        {
            functionDeclarations.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = tool.ParametersSchema.DeepClone(),
            });
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = contents,
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = functionDeclarations }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.15,
                ["maxOutputTokens"] = _options.MaxOutputTokens,
            },
        };

        var (candidateJson, usage) = await CallAsync(body, ct);
        var part = candidateJson?["parts"]?.AsArray().FirstOrDefault();
        var functionCall = part?["functionCall"];
        if (functionCall is not null)
        {
            var name = functionCall["name"]?.GetValue<string>();
            var args = functionCall["args"] as JsonObject ?? new JsonObject();
            return new GeminiToolResult(name, args, null, usage.input, usage.output);
        }

        var text = ExtractText(candidateJson) ?? "I'm not sure how to help with that.";
        return new GeminiToolResult(null, null, text, usage.input, usage.output);
    }

    private async Task<(JsonObject? candidate, (int input, int output) usage)> CallAsync(JsonObject body, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new AIServiceUnavailableException(
                "The Gemini API key is not configured on the server (Gemini:ApiKey / Gemini__ApiKey).");
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/{_options.Model}:generateContent?key={Uri.EscapeDataString(_options.ApiKey)}";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AIServiceUnavailableException("The AI request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AIServiceUnavailableException("Could not reach the AI service.", ex);
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini call failed: {Status} {Body}", (int)response.StatusCode, Truncate(raw));
            if ((int)response.StatusCode is 429 or >= 500)
            {
                throw new AIServiceUnavailableException("The AI service is temporarily unavailable.");
            }
            throw new AIServiceException($"The AI service rejected the request ({(int)response.StatusCode}).");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new AIServiceException("The AI service returned a response that could not be parsed.", ex);
        }

        var candidate = root?["candidates"]?.AsArray().FirstOrDefault()?["content"] as JsonObject;
        var usageNode = root?["usageMetadata"];
        var usage = (
            input: usageNode?["promptTokenCount"]?.GetValue<int>() ?? 0,
            output: usageNode?["candidatesTokenCount"]?.GetValue<int>() ?? 0);
        return (candidate, usage);
    }

    private static string? ExtractText(JsonObject? candidateContent)
    {
        var parts = candidateContent?["parts"]?.AsArray();
        if (parts is null)
        {
            return null;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            var t = part?["text"]?.GetValue<string>();
            if (t is not null)
            {
                sb.Append(t);
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
