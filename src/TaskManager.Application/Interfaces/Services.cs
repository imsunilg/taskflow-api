using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Application.Interfaces;

public record SpeechToTextResult(string Transcript, string Provider);

public class SpeechToTextException : Exception
{
    public SpeechToTextException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface ISpeechToTextService
{
    Task<SpeechToTextResult> TranscribeAsync(Stream audio, CancellationToken ct);
}

public record ExtractedTaskFields(
    string? Title,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    Priority? Priority,
    int? ReminderOffsetMinutes);

public interface ITaskFieldExtractor
{
    ExtractedTaskFields Extract(string transcript);
}

public interface IRecurrenceService
{
    DateOnly? ComputeNextDueDate(RecurrenceRuleEntity rule, DateOnly current);
}

public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationEntity notification, CancellationToken ct);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public record TokenPair(string AccessToken, string RefreshToken);

/// <summary>What a valid refresh token proves: the user id it was issued for and the security
/// stamp captured at issue time (compared against the user's current stamp to reject stale tokens).</summary>
public record RefreshTokenPrincipal(Guid UserId, string SecurityStamp);

public interface IJwtTokenService
{
    TokenPair Issue(UserEntity user);
    RefreshTokenPrincipal? ValidateRefreshToken(string refreshToken);
}

public interface IIdentityProvider
{
    Task<UserEntity?> AuthenticateAsync(string email, string password, CancellationToken ct);
}

public record EmailMessage(string ToEmail, string Subject, string HtmlBody, string TextBody);

/// <summary>Thrown when an email cannot be delivered. The message is safe to log but never
/// contains the email body or any secret.</summary>
public class EmailDeliveryException : Exception
{
    public EmailDeliveryException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IEmailSender
{
    /// <summary>True when SMTP is configured well enough to attempt delivery.</summary>
    bool IsConfigured { get; }
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public interface ICaptchaVerifier
{
    /// <summary>The provider the SPA should render ("turnstile" | "recaptcha" | "hcaptcha" | "disabled").</summary>
    string Provider { get; }
    /// <summary>Public site key for the SPA widget (never the secret). Empty when disabled.</summary>
    string SiteKey { get; }
    /// <summary>DEV ONLY: the token the SPA should send when the provider is "disabled".
    /// Empty in every other case (including outside Development).</summary>
    string DevBypassToken { get; }
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

public interface ILookupCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct);
    Task SetAsync<T>(string key, T value, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);

    static string CategoriesKey(Guid userId) => $"categories:{userId}";
    static string TagsKey(Guid userId) => $"tags:{userId}";
}
