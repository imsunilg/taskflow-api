using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using TaskManager.Api.Hubs;
using TaskManager.Api.Middleware;
using TaskManager.Api.Services;
using TaskManager.Application;
using TaskManager.Application.Interfaces;
using TaskManager.Infrastructure;
using TaskManager.Infrastructure.BackgroundJobs;
using TaskManager.Infrastructure.Identity;
using TaskManager.Infrastructure.Persistence;
using TaskManager.Infrastructure.Persistence.Seeding;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddScoped<INotificationDispatcher, SignalRNotificationDispatcher>();

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "TaskFlow API", Version = "v1" });

    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT access token from POST /api/auth/login (no 'Bearer ' prefix).",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    options.AddSecurityDefinition("Bearer", scheme);
    options.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});
builder.Services.AddSignalR();

// --- Authentication ---
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
jwt.Secret = builder.Configuration["JWT_SECRET"]
             ?? Environment.GetEnvironmentVariable("JWT_SECRET")
             ?? jwt.Secret;
builder.Services.Configure<JwtOptions>(o =>
{
    o.Secret = jwt.Secret;
    o.Issuer = jwt.Issuer;
    o.Audience = jwt.Audience;
    o.AccessTokenMinutes = jwt.AccessTokenMinutes;
    o.RefreshTokenDays = jwt.RefreshTokenDays;
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            // The default inbound-claim map turns the JWT "role" claim into
            // ClaimTypes.Role, which is what [Authorize(Roles=...)] / IsInRole use.
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            // Reject a token the moment its account is deactivated or its password changes
            // (security stamp rotates) — no waiting for the 15-minute expiry.
            OnTokenValidated = async context =>
            {
                var sub = context.Principal?.FindFirst("sub")?.Value
                          ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId))
                {
                    context.Fail("Invalid subject.");
                    return;
                }
                var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                var snapshot = await users.GetSecuritySnapshotAsync(userId, context.HttpContext.RequestAborted);
                if (snapshot is null || !snapshot.IsActive)
                {
                    context.Fail("Account is inactive.");
                    return;
                }
                var stamp = context.Principal?.FindFirst("stamp")?.Value;
                if (!string.Equals(stamp, snapshot.SecurityStamp, StringComparison.Ordinal))
                {
                    context.Fail("Session superseded.");
                }
            },
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin")));

// --- CORS ---
// Explicitly configured origin(s) are always allowed; on top of that any
// http(s)://localhost:<port> or 127.0.0.1 origin is allowed (dev convenience).
var allowedOrigin = builder.Configuration["ALLOWED_ORIGIN"]
                    ?? builder.Configuration["Cors:AllowedOrigin"]
                    ?? "http://localhost:4200";
var configuredOrigins = allowedOrigin
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy
        .SetIsOriginAllowed(origin =>
        {
            if (configuredOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && uri.Host is "localhost" or "127.0.0.1" or "[::1]";
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

// --- Rate limiting ---
// Enabled by default; integration tests disable it so the shared test server
// isn't throttled across the whole suite.
var rateLimitingEnabled = builder.Configuration.GetValue("RateLimiting:Enabled", true);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("general", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("voice", o =>
    {
        o.PermitLimit = 10;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 0;
    });
    // Per-minute burst guard for /api/ai/*. The per-user daily cap (Gemini:DailyRequestCap,
    // enforced by AiUsageLoggingBehavior) is the primary quota; this just smooths bursts.
    options.AddPolicy("ai", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User?.FindFirst("sub")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    // Brute-force protection for sensitive auth actions (e.g. change-password's
    // current-password check). Partitioned by user id, falling back to client IP.
    options.AddPolicy("sensitive", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User?.FindFirst("sub")?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    // Forgot-password: partitioned by client IP (per-email throttling is enforced in the handler).
    options.AddPolicy("password-reset", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
            }));
});

builder.Services.AddResponseCompression();

var healthChecks = builder.Services.AddHealthChecks()
    .AddDbContextCheck<TaskManagerDbContext>("database");
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Redis")))
{
    healthChecks.AddCheck<TaskManager.Api.Services.RedisHealthCheck>("redis");
}

// --- Hangfire ---
var pgConn = builder.Configuration.GetConnectionString("Postgres")!;
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(pgConn)));
builder.Services.AddHangfireServer();

var app = builder.Build();

app.Logger.LogInformation(
    "Lookup cache backend: {Backend}",
    app.Services.GetRequiredService<TaskManager.Infrastructure.Caching.CacheBackendInfo>().Backend);

var voiceCfg = app.Services.GetRequiredService<TaskManager.Infrastructure.Speech.VoiceTranscriptionConfig>();
if (voiceCfg.ConfigurationError is { } voiceError)
{
    app.Logger.LogError("Voice transcription misconfigured: {Error}", voiceError);
}
else
{
    app.Logger.LogInformation(
        "Voice transcription mode: {Mode}{CloudState}",
        voiceCfg.Mode,
        voiceCfg.Mode == TaskManager.Infrastructure.Speech.VoiceTranscriptionMode.Cloud
            ? " (cloud provider configured)"
            : string.Empty);
}

using (var startupScope = app.Services.CreateScope())
{
    var email = startupScope.ServiceProvider.GetRequiredService<IEmailSender>();
    var captcha = startupScope.ServiceProvider.GetRequiredService<ICaptchaVerifier>();
    app.Logger.LogInformation(
        "Email delivery: {EmailState}. CAPTCHA provider: {CaptchaProvider}.",
        email.IsConfigured ? "configured" : "NOT configured (password-reset emails will fail)",
        captcha.Provider);
    if (captcha.Provider == "disabled" && !app.Environment.IsDevelopment())
    {
        app.Logger.LogWarning("CAPTCHA is disabled outside Development — forgot-password requests will be rejected.");
    }

    var gemini = startupScope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TaskManager.Infrastructure.AI.GeminiOptions>>().Value;
    app.Logger.LogInformation(
        "AI (Gemini): {State}. Model: {Model}.",
        gemini.IsConfigured ? "configured" : "NOT configured (all /api/ai/* endpoints will return 503 until Gemini:ApiKey is set)",
        gemini.Model);
}

app.UseSwagger();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "TaskFlow API v1");
    o.DocumentTitle = "TaskFlow API";
});

app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationMiddleware>();

app.UseResponseCompression();
app.UseCors();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<MustChangePasswordMiddleware>();

var controllerEndpoints = app.MapControllers();
if (rateLimitingEnabled)
{
    controllerEndpoints.RequireRateLimiting("general");
}
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapHangfireDashboard("/hangfire");
}

RecurringJob.AddOrUpdate<ReminderDeliveryJob>(
    "reminder-delivery",
    job => job.RunAsync(CancellationToken.None),
    Cron.Minutely);

RecurringJob.AddOrUpdate<TaskManager.Infrastructure.BackgroundJobs.AiMorningPlanJob>(
    "ai-morning-daily-plan",
    job => job.RunAsync(CancellationToken.None),
    Cron.Daily(7, 0));

RecurringJob.AddOrUpdate<TaskManager.Infrastructure.BackgroundJobs.AiEveningSummaryJob>(
    "ai-evening-summary",
    job => job.RunAsync(CancellationToken.None),
    Cron.Daily(20, 0));

// One bootstrap path shared with TaskManager.DbRunner: migrate (dev — prod migrates as a
// deploy step / via the DbRunner), seed the baseline Admin, and seed demo data only when it
// is explicitly enabled and the environment is not Production. Idempotent; never drops/resets.
await DatabaseBootstrapper.RunAsync(
    app.Services,
    app.Environment,
    new BootstrapOptions
    {
        ApplyMigrations = app.Environment.IsDevelopment(),
        SeedBaseline = true,
        SeedDemoData = DemoDataSeeder.IsEnabled(app.Configuration, args),
    },
    app.Logger);

app.Run();

public partial class Program { }
