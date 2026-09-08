using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Auth;

public record UserDto(Guid Id, string Email, string DisplayName);
public record AuthResultDto(string AccessToken, string RefreshToken, UserDto User, bool MustChangePassword);

/// <summary>Returned by change-password: a fresh access token + the user, never the old hash or refresh token string.</summary>
public record ChangePasswordResultDto(string AccessToken, UserDto User);

public record LoginCommand(string Email, string Password) : IRequest<Result<AuthResultDto>>;
public record RefreshCommand(string? RefreshToken) : IRequest<Result<AuthResultDto>>;
public record ChangePasswordCommand(string CurrentPassword, string NewPassword, string ConfirmNewPassword)
    : IRequest<Result<AuthResultDto>>;
public record ForgotPasswordCommand(string Email, string? CaptchaToken, string? RequestIp)
    : IRequest<Result<string>>;
public record CompletePasswordResetCommand(string NewPassword, string ConfirmNewPassword)
    : IRequest<Result<AuthResultDto>>;

public static class PasswordPolicy
{
    // At least 8 chars (max 128), with one lowercase, one uppercase and one digit.
    public static readonly Regex Pattern = new(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,128}$", RegexOptions.Compiled);
    public const string Message =
        "Password must be 8-128 characters and include an uppercase letter, a lowercase letter and a number.";
    public static bool IsValid(string? value) => value is not null && Pattern.IsMatch(value);
}

/// <summary>Cryptographically secure, human-typable one-time passwords. Not a numeric sequence.</summary>
public static class OtpGenerator
{
    // Crockford-style base32 minus vowels and ambiguous glyphs (no 0/O, 1/I/L).
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
    private const int Length = 10;

    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        // Group as XXXXX-XXXXX for readability in the email.
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }

    /// <summary>Accepts the code with or without the dash / surrounding whitespace / casing.</summary>
    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        // Accepts an email address or a plain username (e.g. the seeded "admin").
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class ForgotPasswordCommandValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Enter your current password.");
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("Enter a new password.")
            .Must(PasswordPolicy.IsValid).WithMessage(PasswordPolicy.Message)
            .NotEqual(x => x.CurrentPassword).WithMessage("The new password must be different from your current password.");
        RuleFor(x => x.ConfirmNewPassword)
            .NotEmpty().WithMessage("Confirm your new password.")
            .Equal(x => x.NewPassword).WithMessage("Passwords do not match.");
    }
}

public class CompletePasswordResetCommandValidator : AbstractValidator<CompletePasswordResetCommand>
{
    public CompletePasswordResetCommandValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage("Enter a new password.")
            .Must(PasswordPolicy.IsValid).WithMessage(PasswordPolicy.Message);
        RuleFor(x => x.ConfirmNewPassword)
            .NotEmpty().WithMessage("Confirm your new password.")
            .Equal(x => x.NewPassword).WithMessage("Passwords do not match.");
    }
}

public class AuthHandlers :
    IRequestHandler<LoginCommand, Result<AuthResultDto>>,
    IRequestHandler<RefreshCommand, Result<AuthResultDto>>
{
    private const string InvalidCredentials = "Invalid email or password.";
    private const int MaxOtpAttempts = 5;

    private readonly IIdentityProvider _identity;
    private readonly IUserRepository _users;
    private readonly IPasswordResetRepository _resets;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<AuthHandlers> _logger;

    public AuthHandlers(
        IIdentityProvider identity,
        IUserRepository users,
        IPasswordResetRepository resets,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IUnitOfWork uow,
        ILogger<AuthHandlers> logger)
    {
        _identity = identity;
        _users = users;
        _resets = resets;
        _hasher = hasher;
        _jwt = jwt;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AuthResultDto>> Handle(LoginCommand c, CancellationToken ct)
    {
        var user = await _identity.AuthenticateAsync(c.Email, c.Password, ct);
        if (user is null)
        {
            return await TryOneTimePasswordLoginAsync(c.Email, c.Password, ct);
        }

        if (!user.IsActive)
        {
            return Result<AuthResultDto>.Failure("This account is inactive. Contact an administrator.");
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _uow.CommitAsync(ct);

        return Ok(user, user.MustChangePassword, "Login successful");
    }

    private async Task<Result<AuthResultDto>> TryOneTimePasswordLoginAsync(string email, string candidate, CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(email, ct);
        if (user is null || !user.IsActive)
        {
            return Result<AuthResultDto>.Failure(InvalidCredentials);
        }

        var now = DateTime.UtcNow;
        var reset = await _resets.GetUsableForUserAsync(user.Id, now, ct);
        if (reset is null)
        {
            return Result<AuthResultDto>.Failure(InvalidCredentials);
        }

        var normalized = OtpGenerator.Normalize(candidate ?? string.Empty);
        if (!_hasher.Verify(normalized, reset.OtpHash))
        {
            reset.AttemptCount++;
            if (reset.AttemptCount >= MaxOtpAttempts)
            {
                reset.UsedAt = now; // burn it after too many wrong guesses
            }
            await _uow.CommitAsync(ct);
            return Result<AuthResultDto>.Failure(InvalidCredentials);
        }

        // Valid OTP — consume it, force a password change, keep the user out of everything else.
        reset.UsedAt = now;
        user.MustChangePassword = true;
        user.LastLoginAt = now;
        await _users.AddStatusChangeAsync(Audit(user, "PasswordResetOtpUsed", now), ct);
        await _uow.CommitAsync(ct);

        _logger.LogInformation("User {UserId} signed in with a one-time reset password.", user.Id);
        return Ok(user, mustChangePassword: true, "Signed in. Set a new password to continue.");
    }

    public async Task<Result<AuthResultDto>> Handle(RefreshCommand c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.RefreshToken))
        {
            return Result<AuthResultDto>.Failure("Missing refresh token.");
        }

        var principal = _jwt.ValidateRefreshToken(c.RefreshToken);
        if (principal is null)
        {
            return Result<AuthResultDto>.Failure("Invalid or expired refresh token.");
        }

        var user = await _users.GetByIdAsync(principal.UserId, ct);
        if (user is null)
        {
            return Result<AuthResultDto>.Failure("User no longer exists.");
        }

        if (!user.IsActive)
        {
            return Result<AuthResultDto>.Failure("This account is inactive. Contact an administrator.");
        }

        if (!string.Equals(principal.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            return Result<AuthResultDto>.Failure("Invalid or expired refresh token.");
        }

        return Ok(user, user.MustChangePassword, "Token refreshed");
    }

    private Result<AuthResultDto> Ok(UserEntity user, bool mustChangePassword, string message)
    {
        var tokens = _jwt.Issue(user);
        return Result<AuthResultDto>.Success(
            new AuthResultDto(tokens.AccessToken, tokens.RefreshToken,
                new UserDto(user.Id, user.Email, user.DisplayName), mustChangePassword),
            message);
    }

    internal static UserStatusChangeEntity Audit(UserEntity user, string action, DateTime at, string? details = null) => new()
    {
        TargetUserId = user.Id,
        ChangedByUserId = user.Id,
        Action = action,
        PreviousIsActive = user.IsActive,
        NewIsActive = user.IsActive,
        Details = details,
        ChangedAt = at,
    };
}

public class ForgotPasswordHandler : IRequestHandler<ForgotPasswordCommand, Result<string>>
{
    // Identical response whether or not the email exists — no account enumeration.
    private const string Generic = "If an account exists for this email address, we have sent reset instructions.";
    private const int MaxRequestsPer15Min = 3;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly IUserRepository _users;
    private readonly IPasswordResetRepository _resets;
    private readonly IPasswordHasher _hasher;
    private readonly ICaptchaVerifier _captcha;
    private readonly IEmailSender _email;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ForgotPasswordHandler> _logger;

    public ForgotPasswordHandler(
        IUserRepository users,
        IPasswordResetRepository resets,
        IPasswordHasher hasher,
        ICaptchaVerifier captcha,
        IEmailSender email,
        IUnitOfWork uow,
        ILogger<ForgotPasswordHandler> logger)
    {
        _users = users;
        _resets = resets;
        _hasher = hasher;
        _captcha = captcha;
        _email = email;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<string>> Handle(ForgotPasswordCommand cmd, CancellationToken ct)
    {
        if (!await _captcha.VerifyAsync(cmd.CaptchaToken, cmd.RequestIp, ct))
        {
            return Result<string>.Failure(
                "CAPTCHA verification failed. Please try again.",
                new[] { new ValidationError("captchaToken", "CAPTCHA verification failed. Please try again.") });
        }

        var email = cmd.Email.Trim().ToLowerInvariant();
        var user = await _users.GetByEmailAsync(email, ct);

        // Deactivated / deleted / unknown users get the generic answer and nothing is sent.
        if (user is null || !user.IsActive)
        {
            return Result<string>.Success(Generic);
        }

        var now = DateTime.UtcNow;

        // Per-email rate limit (IP rate limiting is enforced by the "password-reset" limiter policy).
        if (await _resets.CountForUserSinceAsync(user.Id, now - TimeSpan.FromMinutes(15), ct) >= MaxRequestsPer15Min)
        {
            _logger.LogWarning("Password reset throttled for user {UserId} (too many requests).", user.Id);
            return Result<string>.Success(Generic);
        }

        await _resets.InvalidateActiveForUserAsync(user.Id, now, ct);

        var otp = OtpGenerator.Generate();
        var expiresAt = now + Ttl;
        await _resets.AddAsync(new PasswordResetEntity
        {
            UserId = user.Id,
            OtpHash = _hasher.Hash(OtpGenerator.Normalize(otp)),
            ExpiresAt = expiresAt,
            RequestIp = string.IsNullOrWhiteSpace(cmd.RequestIp) ? null : cmd.RequestIp,
        }, ct);
        await _users.AddStatusChangeAsync(AuthHandlers.Audit(user, "PasswordResetRequested", now), ct);
        await _uow.CommitAsync(ct);

        try
        {
            await _email.SendAsync(ResetEmail.Build(user.DisplayName, user.Email, otp, expiresAt), ct);
        }
        catch (EmailDeliveryException ex)
        {
            // Never surface SMTP detail or the existence of the account to the caller.
            _logger.LogError("Password reset email not delivered for user {UserId}: {Reason}", user.Id, ex.Message);
        }

        return Result<string>.Success(Generic);
    }
}

public class ChangePasswordHandler : IRequestHandler<ChangePasswordCommand, Result<AuthResultDto>>
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ChangePasswordHandler> _logger;

    public ChangePasswordHandler(
        IUserRepository users,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        ICurrentUserService currentUser,
        IUnitOfWork uow,
        ILogger<ChangePasswordHandler> logger)
    {
        _users = users;
        _hasher = hasher;
        _jwt = jwt;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AuthResultDto>> Handle(ChangePasswordCommand cmd, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Result<AuthResultDto>.Failure("Not authenticated.");
        }

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return Result<AuthResultDto>.Failure("User not found.");
        }

        if (!_hasher.Verify(cmd.CurrentPassword, user.PasswordHash))
        {
            return Result<AuthResultDto>.Failure(
                "Current password is incorrect.",
                new[] { new ValidationError("currentPassword", "Current password is incorrect.") });
        }

        var now = DateTime.UtcNow;
        user.PasswordHash = _hasher.Hash(cmd.NewPassword);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N");

        await _users.AddStatusChangeAsync(AuthHandlers.Audit(user, "PasswordChanged", now), ct);
        await _uow.CommitAsync(ct);

        _logger.LogInformation("User {UserId} changed their password at {ChangedAt}", user.Id, now);

        var tokens = _jwt.Issue(user);
        return Result<AuthResultDto>.Success(
            new AuthResultDto(tokens.AccessToken, tokens.RefreshToken,
                new UserDto(user.Id, user.Email, user.DisplayName), false),
            "Your password has been changed.");
    }
}

public class CompletePasswordResetHandler : IRequestHandler<CompletePasswordResetCommand, Result<AuthResultDto>>
{
    private readonly IUserRepository _users;
    private readonly IPasswordResetRepository _resets;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<CompletePasswordResetHandler> _logger;

    public CompletePasswordResetHandler(
        IUserRepository users,
        IPasswordResetRepository resets,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        ICurrentUserService currentUser,
        IUnitOfWork uow,
        ILogger<CompletePasswordResetHandler> logger)
    {
        _users = users;
        _resets = resets;
        _hasher = hasher;
        _jwt = jwt;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AuthResultDto>> Handle(CompletePasswordResetCommand cmd, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Result<AuthResultDto>.Failure("Not authenticated.");
        }

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return Result<AuthResultDto>.Failure("User not found.");
        }

        if (!user.MustChangePassword)
        {
            return Result<AuthResultDto>.Failure("No password reset is pending for this account.");
        }

        if (_hasher.Verify(cmd.NewPassword, user.PasswordHash))
        {
            return Result<AuthResultDto>.Failure(
                "The new password must be different from your previous password.",
                new[] { new ValidationError("newPassword", "Choose a password you haven't used before.") });
        }

        var now = DateTime.UtcNow;
        user.PasswordHash = _hasher.Hash(cmd.NewPassword);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // invalidate every other session

        await _resets.InvalidateActiveForUserAsync(user.Id, now, ct);
        await _users.AddStatusChangeAsync(AuthHandlers.Audit(user, "PasswordResetCompleted", now), ct);
        await _uow.CommitAsync(ct);

        _logger.LogInformation("User {UserId} completed a password reset at {At}", user.Id, now);

        var tokens = _jwt.Issue(user);
        return Result<AuthResultDto>.Success(
            new AuthResultDto(tokens.AccessToken, tokens.RefreshToken,
                new UserDto(user.Id, user.Email, user.DisplayName), false),
            "Your new password is set.");
    }
}

/// <summary>Builds the TaskFlow password-reset email. The OTP is embedded in the body but the
/// body itself is never written to logs.</summary>
internal static class ResetEmail
{
    public static EmailMessage Build(string displayName, string toEmail, string otp, DateTime expiresAtUtc)
    {
        var expires = expiresAtUtc.ToString("HH:mm 'UTC' 'on' d MMM yyyy");
        var name = string.IsNullOrWhiteSpace(displayName) ? "there" : displayName;

        var text = new StringBuilder()
            .AppendLine($"Hi {name},")
            .AppendLine()
            .AppendLine("We received a request to reset your TaskFlow password.")
            .AppendLine()
            .AppendLine($"Temporary one-time password:  {otp}")
            .AppendLine($"This code expires at {expires} (about 15 minutes).")
            .AppendLine()
            .AppendLine("Sign in with this temporary password, then create a new password immediately.")
            .AppendLine()
            .AppendLine("Never share this code with anyone. TaskFlow staff will not ask you for it.")
            .AppendLine("If you did not request a reset, you can ignore this email — your password is unchanged.")
            .AppendLine()
            .AppendLine("— The TaskFlow team")
            .ToString();

        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:520px;margin:0 auto;color:#1f2430">
              <h2 style="margin:0 0 4px">Reset your TaskFlow password</h2>
              <p>Hi {WebEncode(name)},</p>
              <p>We received a request to reset your password. Use this temporary one-time password to sign in:</p>
              <p style="font-size:22px;letter-spacing:2px;font-weight:700;background:#f2f4f8;
                        border-radius:8px;padding:14px 18px;text-align:center">{WebEncode(otp)}</p>
              <p><strong>Expires at {WebEncode(expires)}</strong> (about 15 minutes).</p>
              <p><strong>Sign in with this temporary password, then create a new password immediately.</strong></p>
              <p style="color:#8a2b2b">Never share this code with anyone. If you did not request this, ignore
                 this email — your password has not changed.</p>
              <p style="color:#6b7280;font-size:13px">— The TaskFlow team</p>
            </div>
            """;

        return new EmailMessage(toEmail, "Your TaskFlow password reset code", html, text);
    }

    private static string WebEncode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
