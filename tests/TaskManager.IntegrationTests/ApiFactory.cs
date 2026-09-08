using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TaskManager.Application.Interfaces;
using Xunit;

namespace TaskManager.IntegrationTests;

/// <summary>Minimal <see cref="IHostEnvironment"/> for exercising env-gated code paths.</summary>
public sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

/// <summary>Captures reset emails instead of hitting a real SMTP relay during tests.</summary>
public class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = new();
    public bool IsConfigured => true;
    public IReadOnlyList<EmailMessage> Sent => _sent;
    public EmailMessage? LastTo(string email) => _sent.LastOrDefault(m =>
        string.Equals(m.ToEmail, email, StringComparison.OrdinalIgnoreCase));

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Spins up the real API against a local PostgreSQL test database. If PostgreSQL is not
/// reachable the whole collection is skipped (so CI without a DB service stays green).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string BaseConn =
        "Host=localhost;Port=5432;Username=postgres;Password=284228";
    private const string TestDb = "taskflow_inttest";

    public bool DatabaseAvailable { get; private set; }

    public readonly CapturingEmailSender Emails = new();

    public async Task InitializeAsync()
    {
        try
        {
            await using var admin = new NpgsqlConnection($"{BaseConn};Database=postgres");
            await admin.OpenAsync();
            await using (var drop = admin.CreateCommand())
            {
                drop.CommandText = $"DROP DATABASE IF EXISTS {TestDb} WITH (FORCE);";
                await drop.ExecuteNonQueryAsync();
            }
            await using (var create = admin.CreateCommand())
            {
                create.CommandText = $"CREATE DATABASE {TestDb};";
                await create.ExecuteNonQueryAsync();
            }
            DatabaseAvailable = true;
        }
        catch
        {
            DatabaseAvailable = false;
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = $"{BaseConn};Database={TestDb}",
                ["ConnectionStrings:Redis"] = "",
                ["SpeechToText:Provider"] = "None",
                ["RateLimiting:Enabled"] = "false",
                ["Jwt:Secret"] = "integration-tests-signing-secret-that-is-long-enough",
                ["Email:Enabled"] = "true",
                ["Email:SmtpHost"] = "unused-in-tests",
                ["Email:FromEmail"] = "noreply@taskflow.test",
                ["Captcha:Provider"] = "disabled",
                ["Captcha:DevBypassToken"] = "test-bypass",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
        return base.CreateHost(builder);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
