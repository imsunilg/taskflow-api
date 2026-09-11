using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;
using TaskManager.Application.AI;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Infrastructure.AI;
using TaskManager.Infrastructure.Caching;
using TaskManager.Infrastructure.Identity;
using TaskManager.Infrastructure.NLP;
using TaskManager.Infrastructure.Persistence;
using TaskManager.Infrastructure.Repositories;
using TaskManager.Infrastructure.Services;
using TaskManager.Infrastructure.Speech;

namespace TaskManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Database=taskflow;Username=postgres;Password=284228";

        services.AddDbContext<TaskManagerDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<TaskManagerDbContext>());

        services.AddScoped<ITaskRepository, TaskRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<IReminderRepository, ReminderRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IVoiceRecordingRepository, VoiceRecordingRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordResetRepository, PasswordResetRepository>();
        services.AddScoped<IAiRepository, AiRepository>();

        services.AddScoped<IRecurrenceService, RecurrenceService>();
        services.AddScoped<ITaskFieldExtractor, SimpleTaskFieldExtractor>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IIdentityProvider, DatabaseIdentityProvider>();

        services.Configure<JwtOptions>(config.GetSection("Jwt"));

        // --- Email (SMTP) + CAPTCHA ---
        services.Configure<Email.EmailOptions>(config.GetSection("Email"));
        services.AddScoped<IEmailSender, Email.SmtpEmailSender>();

        services.Configure<Security.CaptchaOptions>(config.GetSection("Captcha"));
        services.AddHttpClient<ICaptchaVerifier, Security.HttpCaptchaVerifier>()
            .AddPolicyHandler(RetryPolicy());
        var sttOptions = new SpeechToTextOptions();
        config.GetSection("SpeechToText").Bind(sttOptions);
        services.Configure<SpeechToTextOptions>(config.GetSection("SpeechToText"));

        var voiceConfig = VoiceTranscriptionConfig.From(sttOptions);
        services.AddSingleton(voiceConfig);

        switch (voiceConfig.Mode)
        {
            case VoiceTranscriptionMode.Cloud:
                // Even if not fully configured we register the client; the controller
                // short-circuits with 503 before it is ever called when CloudConfigured is false.
                services.AddHttpClient<ISpeechToTextService, CloudSpeechToTextService>()
                    .AddPolicyHandler(RetryPolicy());
                break;
            case VoiceTranscriptionMode.Demo:
                // DEVELOPMENT ONLY — returns a canned sample sentence, never real speech.
                services.AddScoped<ISpeechToTextService, DemoSpeechToTextService>();
                break;
            default:
                // Browser / Disabled: the server never transcribes audio. This impl only
                // throws an honest error if the upload path is somehow reached.
                services.AddScoped<ISpeechToTextService, UnconfiguredSpeechToTextService>();
                break;
        }

        // Caching: Redis is optional. When no "Redis" connection string is configured
        // (the default), the app uses the built-in in-process system memory cache —
        // no external cache server is contacted.
        var redis = config.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddStackExchangeRedisCache(o => o.Configuration = redis);
        }
        else
        {
            services.AddDistributedMemoryCache();
        }
        services.AddSingleton<CacheBackendInfo>(new CacheBackendInfo(
            string.IsNullOrWhiteSpace(redis) ? "InMemory" : "Redis"));
        services.AddScoped<ILookupCache, LookupCache>();

        services.AddScoped<BackgroundJobs.ReminderDeliveryJob>();

        // --- AI (Gemini) ---
        // ApiKey must come from `dotnet user-secrets set "Gemini:ApiKey" "..."` in Development or
        // the Gemini__ApiKey environment variable elsewhere — appsettings*.json only ever carries
        // placeholders for the other, non-secret fields.
        services.Configure<GeminiOptions>(config.GetSection("Gemini"));
        services.Configure<AiQuotaOptions>(config.GetSection("Gemini"));
        services.AddHttpClient<GeminiClient>()
            .AddPolicyHandler(GeminiRetryPolicy())
            .AddPolicyHandler(CircuitBreakerPolicy());
        services.AddScoped<IAIService, GeminiAIService>();
        services.AddScoped<BackgroundJobs.AiMorningPlanJob>();
        services.AddScoped<BackgroundJobs.AiEveningSummaryJob>();

        return services;
    }

    private static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == HttpStatusCode.RequestTimeout)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)));

    private static IAsyncPolicy<HttpResponseMessage> GeminiRetryPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(2, attempt => TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt)));

    /// <summary>Gemini-specific: after the 2 retries above still fail 5 times in a row, trip for 30s.</summary>
    private static IAsyncPolicy<HttpResponseMessage> CircuitBreakerPolicy() =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
            .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
}
