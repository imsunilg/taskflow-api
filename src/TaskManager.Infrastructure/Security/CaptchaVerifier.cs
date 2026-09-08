using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskManager.Application.Interfaces;

namespace TaskManager.Infrastructure.Security;

public class CaptchaOptions
{
    /// <summary>turnstile | recaptcha | hcaptcha | disabled</summary>
    public string Provider { get; set; } = "disabled";
    public string SiteKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// DEVELOPMENT ONLY. When the provider is "disabled", a request carrying exactly this token
    /// passes verification. Ignored entirely outside the Development environment.
    /// </summary>
    public string DevBypassToken { get; set; } = "dev-captcha-bypass";
}

public class HttpCaptchaVerifier : ICaptchaVerifier
{
    private static readonly Dictionary<string, string> VerifyUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["turnstile"] = "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        ["recaptcha"] = "https://www.google.com/recaptcha/api/siteverify",
        ["hcaptcha"] = "https://api.hcaptcha.com/siteverify",
    };

    private readonly HttpClient _http;
    private readonly CaptchaOptions _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<HttpCaptchaVerifier> _logger;

    public HttpCaptchaVerifier(
        HttpClient http, IOptions<CaptchaOptions> options, IHostEnvironment env, ILogger<HttpCaptchaVerifier> logger)
    {
        _http = http;
        _options = options.Value;
        _env = env;
        _logger = logger;
    }

    private bool ProviderConfigured =>
        VerifyUrls.ContainsKey(_options.Provider) && !string.IsNullOrWhiteSpace(_options.SecretKey);

    public string Provider => ProviderConfigured ? _options.Provider.ToLowerInvariant() : "disabled";

    public string SiteKey => ProviderConfigured ? _options.SiteKey : string.Empty;

    public string DevBypassToken =>
        !ProviderConfigured && _env.IsDevelopment() ? _options.DevBypassToken : string.Empty;

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        if (!ProviderConfigured)
        {
            // No real provider: only the dev bypass token works, and only in Development.
            if (_env.IsDevelopment())
            {
                return !string.IsNullOrEmpty(_options.DevBypassToken)
                       && string.Equals(token, _options.DevBypassToken, StringComparison.Ordinal);
            }
            _logger.LogError("CAPTCHA verification requested but no provider is configured outside Development.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["secret"] = _options.SecretKey,
                ["response"] = token,
            };
            if (!string.IsNullOrWhiteSpace(remoteIp))
            {
                form["remoteip"] = remoteIp;
            }

            using var response = await _http.PostAsync(
                VerifyUrls[_options.Provider], new FormUrlEncodedContent(form), ct);
            var payload = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(ct);
            return payload?.Success == true;
        }
        catch (Exception ex)
        {
            _logger.LogError("CAPTCHA verification call failed: {Reason}", ex.Message);
            return false;
        }
    }

    private sealed record SiteVerifyResponse(bool Success);
}
