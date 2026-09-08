using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskManager.Application.Auth;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;

namespace TaskManager.Api.Controllers;

public class AuthController : ApiControllerBase
{
    private const string RefreshCookie = "tf_refresh";

    public record LoginBody(string Email, string Password);
    public record ChangePasswordBody(string CurrentPassword, string NewPassword, string ConfirmNewPassword);
    public record ForgotPasswordBody(string Email, string? CaptchaToken);
    public record CompletePasswordResetBody(string NewPassword, string ConfirmNewPassword);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginBody body, CancellationToken ct)
    {
        var result = await Mediator.Send(new LoginCommand(body.Email, body.Password), ct);
        if (result.IsSuccess)
        {
            SetRefreshCookie(result.Value!.RefreshToken);
        }
        return result.IsSuccess
            ? Ok(ApiResponse<AuthResultDto>.Ok(result.Value!, result.Message))
            : Unauthorized(ApiResponse<AuthResultDto>.Fail(result.Message));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        Request.Cookies.TryGetValue(RefreshCookie, out var token);
        var result = await Mediator.Send(new RefreshCommand(token), ct);
        if (result.IsSuccess)
        {
            SetRefreshCookie(result.Value!.RefreshToken);
            return Ok(ApiResponse<AuthResultDto>.Ok(result.Value!, result.Message));
        }
        return Unauthorized(ApiResponse<AuthResultDto>.Fail(result.Message));
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(RefreshCookie);
        return Ok(ApiResponse<object>.Ok(new { }, "Logged out"));
    }

    /// <summary>Public config the SPA needs to render the CAPTCHA widget. Never returns the secret key.</summary>
    [AllowAnonymous]
    [HttpGet("captcha-config")]
    public IActionResult CaptchaConfig([FromServices] ICaptchaVerifier captcha)
        => Ok(ApiResponse<object>.Ok(new
        {
            provider = captcha.Provider,
            siteKey = captcha.SiteKey,
            devBypassToken = captcha.DevBypassToken,
        }));

    /// <summary>
    /// Start a password reset. Always responds the same way whether or not the email exists.
    /// Rate limited by client IP ("password-reset" policy) and by email (in the handler).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("password-reset")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordBody body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await Mediator.Send(new ForgotPasswordCommand(body.Email, body.CaptchaToken, ip), ct);

        return result.IsSuccess
            ? Ok(ApiResponse<string>.Ok(result.Value!, result.Value!))
            : BadRequest(ApiResponse<string>.FromResult(result));
    }

    /// <summary>
    /// Finish a forced password change after a one-time-password login. No current password is
    /// required — the OTP already authenticated the user. Rotates the security stamp (invalidating
    /// every other session) and issues fresh credentials.
    /// </summary>
    [Authorize]
    [EnableRateLimiting("sensitive")]
    [HttpPost("complete-password-reset")]
    public async Task<IActionResult> CompletePasswordReset([FromBody] CompletePasswordResetBody body, CancellationToken ct)
    {
        var result = await Mediator.Send(
            new CompletePasswordResetCommand(body.NewPassword, body.ConfirmNewPassword), ct);

        if (!result.IsSuccess)
        {
            return BadRequest(ApiResponse<ChangePasswordResultDto>.FromResult(
                Result<ChangePasswordResultDto>.Failure(result.Message, result.Errors)));
        }

        SetRefreshCookie(result.Value!.RefreshToken);
        return Ok(ApiResponse<ChangePasswordResultDto>.Ok(
            new ChangePasswordResultDto(result.Value!.AccessToken, result.Value!.User), result.Message));
    }

    /// <summary>
    /// Change the signed-in user's password (Settings flow — current password required).
    /// Invalidates every other session; keeps this one alive with a fresh access token + cookie.
    /// </summary>
    [Authorize]
    [EnableRateLimiting("sensitive")]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordBody body, CancellationToken ct)
    {
        var result = await Mediator.Send(
            new ChangePasswordCommand(body.CurrentPassword, body.NewPassword, body.ConfirmNewPassword), ct);

        if (!result.IsSuccess)
        {
            return BadRequest(ApiResponse<ChangePasswordResultDto>.FromResult(
                Result<ChangePasswordResultDto>.Failure(result.Message, result.Errors)));
        }

        SetRefreshCookie(result.Value!.RefreshToken);
        return Ok(ApiResponse<ChangePasswordResultDto>.Ok(
            new ChangePasswordResultDto(result.Value!.AccessToken, result.Value!.User), result.Message));
    }

    private void SetRefreshCookie(string token)
    {
        Response.Cookies.Append(RefreshCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(14),
        });
    }
}
