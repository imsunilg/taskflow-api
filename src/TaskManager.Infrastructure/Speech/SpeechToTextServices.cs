using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskManager.Application.Interfaces;

namespace TaskManager.Infrastructure.Speech;

public class SpeechToTextOptions
{
    /// <summary>
    /// "Browser" (default) — the Angular app transcribes locally with the Web Speech
    /// API; the server never receives audio.
    /// "Cloud" — the app records audio and uploads it; the server calls the external
    /// provider at <see cref="Endpoint"/> using <see cref="ApiKey"/>.
    /// "Disabled" — voice capture is turned off; the UI shows a "type instead" state.
    /// "Demo" — DEVELOPMENT ONLY, returns a fixed sample sentence. Never for real users.
    /// </summary>
    public string Provider { get; set; } = "Browser";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
}

public enum VoiceTranscriptionMode
{
    Browser,
    Cloud,
    Disabled,
    Demo,
}

/// <summary>
/// Effective, secret-free view of the voice configuration. Safe to expose to the
/// browser via GET /api/voice/config so the SPA can pick the right capture flow.
/// </summary>
public sealed class VoiceTranscriptionConfig
{
    public required VoiceTranscriptionMode Mode { get; init; }

    /// <summary>True only when Mode == Cloud and Endpoint + ApiKey are both present.</summary>
    public required bool CloudConfigured { get; init; }

    public long MaxAudioBytes { get; init; } = 10 * 1024 * 1024;

    public IReadOnlyList<string> AllowedMimeTypes { get; init; } =
        new[] { "audio/webm", "audio/mp4", "audio/wav" };

    /// <summary>Populated when Mode == Cloud but configuration is incomplete (server log / diagnostics only).</summary>
    public string? ConfigurationError { get; init; }

    public static VoiceTranscriptionConfig From(SpeechToTextOptions o)
    {
        var mode = (o.Provider ?? "Browser").Trim().ToLowerInvariant() switch
        {
            "cloud" => VoiceTranscriptionMode.Cloud,
            "disabled" or "off" => VoiceTranscriptionMode.Disabled,
            "demo" => VoiceTranscriptionMode.Demo,
            // "browser" and legacy "none"/"stub"/anything else → browser transcription.
            _ => VoiceTranscriptionMode.Browser,
        };

        var cloudConfigured = mode == VoiceTranscriptionMode.Cloud
            && !string.IsNullOrWhiteSpace(o.Endpoint)
            && !string.IsNullOrWhiteSpace(o.ApiKey);

        string? error = mode == VoiceTranscriptionMode.Cloud && !cloudConfigured
            ? "SpeechToText:Provider is \"Cloud\" but SpeechToText:Endpoint and/or SpeechToText:ApiKey are missing. "
              + "Set them via environment variables (SpeechToText__Endpoint / SpeechToText__ApiKey) or user secrets."
            : null;

        return new VoiceTranscriptionConfig
        {
            Mode = mode,
            CloudConfigured = cloudConfigured,
            ConfigurationError = error,
        };
    }
}

/// <summary>
/// Default backend behaviour: there is no server-side transcription provider, so the
/// server never invents a transcript. Real words are captured in the browser via the
/// Web Speech API; this service exists only so callers get a clear, honest failure if
/// they somehow reach the audio-upload path without a provider configured.
/// </summary>
public class UnconfiguredSpeechToTextService : ISpeechToTextService
{
    public Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken ct) =>
        throw new SpeechToTextException(
            "Server-side speech-to-text is not configured. Transcription is performed in the browser; " +
            "set SpeechToText:Provider to \"Cloud\" (with Endpoint/ApiKey) to enable server transcription.");
}

/// <summary>
/// DEVELOPMENT-ONLY stub that returns a fixed sample sentence. Wired up only when
/// <c>SpeechToText:Provider</c> is explicitly set to <c>"Demo"</c>. It must never be
/// selected in a normal user-facing configuration.
/// </summary>
public class DemoSpeechToTextService : ISpeechToTextService
{
    public Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken ct) =>
        Task.FromResult(new SpeechToTextResult(
            "Remind me to submit the quarterly report tomorrow at 3pm, high priority.",
            "Demo"));
}

public class CloudSpeechToTextService : ISpeechToTextService
{
    private readonly HttpClient _http;
    private readonly SpeechToTextOptions _options;

    public CloudSpeechToTextService(HttpClient http, IOptions<SpeechToTextOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new SpeechToTextException("SpeechToText:Endpoint is not configured.");
        }

        try
        {
            using var content = new MultipartFormDataContent();
            using var audioContent = new StreamContent(audio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(audioContent, "audio", "recording.webm");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = content };
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            }

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new SpeechToTextException($"Provider returned {(int)response.StatusCode}: {body}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var transcript = doc.RootElement.TryGetProperty("transcript", out var t)
                ? t.GetString() ?? string.Empty
                : doc.RootElement.TryGetProperty("text", out var x) ? x.GetString() ?? string.Empty : string.Empty;

            return new SpeechToTextResult(transcript, "Cloud");
        }
        catch (SpeechToTextException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SpeechToTextException("Speech-to-text request failed.", ex);
        }
    }
}
