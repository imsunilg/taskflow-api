using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskManager.Application.Auth;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using Xunit;

namespace TaskManager.UnitTests;

public class OtpGeneratorTests
{
    [Fact]
    public void Generates_a_grouped_non_numeric_code_each_time()
    {
        var a = OtpGenerator.Generate();
        var b = OtpGenerator.Generate();

        a.Should().MatchRegex("^[A-Z2-9]{5}-[A-Z2-9]{5}$");
        a.Should().NotBe(b);
        a.Any(char.IsDigit).Should().BeTrue();      // has entropy from digits
        a.Any(char.IsLetter).Should().BeTrue();
    }

    [Fact]
    public void Normalize_is_case_and_dash_insensitive()
    {
        OtpGenerator.Normalize(" abcde-fghjk ").Should().Be("ABCDEFGHJK");
    }
}

public class ForgotPasswordHandlerTests
{
    private const string Generic = "If an account exists for this email address, we have sent reset instructions.";

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordResetRepository _resets = Substitute.For<IPasswordResetRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ICaptchaVerifier _captcha = Substitute.For<ICaptchaVerifier>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public ForgotPasswordHandlerTests()
    {
        _captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        _hasher.Hash(Arg.Any<string>()).Returns("OTP-HASH");
    }

    private ForgotPasswordHandler Sut() => new(
        _users, _resets, _hasher, _captcha, _email, _uow, NullLogger<ForgotPasswordHandler>.Instance);

    private static UserEntity Active() => new()
    {
        Id = Guid.NewGuid(), Email = "u@u.u", DisplayName = "U", IsActive = true,
    };

    [Fact]
    public async Task Rejects_a_bad_captcha_before_touching_the_database()
    {
        _captcha.VerifyAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Sut().Handle(new ForgotPasswordCommand("u@u.u", "bad", "1.2.3.4"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "captchaToken");
        await _users.DidNotReceive().GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_email_returns_the_generic_message_and_sends_nothing()
    {
        _users.GetByEmailAsync("ghost@u.u", Arg.Any<CancellationToken>()).Returns((UserEntity?)null);

        var result = await Sut().Handle(new ForgotPasswordCommand("ghost@u.u", "ok", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Generic);
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _resets.DidNotReceive().AddAsync(Arg.Any<PasswordResetEntity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deactivated_user_returns_the_generic_message_and_sends_nothing()
    {
        var user = Active();
        user.IsActive = false;
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        var result = await Sut().Handle(new ForgotPasswordCommand(user.Email, "ok", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Generic);
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Active_user_gets_a_hashed_single_use_otp_emailed_and_older_ones_invalidated()
    {
        var user = Active();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _resets.CountForUserSinceAsync(user.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);

        PasswordResetEntity? stored = null;
        _resets.When(x => x.AddAsync(Arg.Any<PasswordResetEntity>(), Arg.Any<CancellationToken>()))
            .Do(ci => stored = ci.Arg<PasswordResetEntity>());
        EmailMessage? sent = null;
        _email.When(x => x.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()))
            .Do(ci => sent = ci.Arg<EmailMessage>());

        var result = await Sut().Handle(new ForgotPasswordCommand(user.Email, "ok", "9.9.9.9"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Generic);
        await _resets.Received(1).InvalidateActiveForUserAsync(user.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        stored!.OtpHash.Should().Be("OTP-HASH");
        stored.UsedAt.Should().BeNull();
        stored.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        stored.RequestIp.Should().Be("9.9.9.9");
        sent!.ToEmail.Should().Be(user.Email);
        // the email body must NOT contain the stored hash, and must carry a raw code + expiry line
        sent.HtmlBody.Should().NotContain("OTP-HASH");
        sent.TextBody.Should().Contain("expires");
        await _users.Received().AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "PasswordResetRequested"), Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Too_many_recent_requests_are_silently_throttled()
    {
        var user = Active();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _resets.CountForUserSinceAsync(user.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(3);

        var result = await Sut().Handle(new ForgotPasswordCommand(user.Email, "ok", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Generic);
        await _resets.DidNotReceive().AddAsync(Arg.Any<PasswordResetEntity>(), Arg.Any<CancellationToken>());
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_email_delivery_failure_does_not_leak_and_still_returns_generic()
    {
        var user = Active();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _resets.CountForUserSinceAsync(user.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(0);
        _email.When(x => x.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new EmailDeliveryException("relay rejected 550"));

        var result = await Sut().Handle(new ForgotPasswordCommand(user.Email, "ok", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Generic);
    }
}

public class CompletePasswordResetHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordResetRepository _resets = Substitute.For<IPasswordResetRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IJwtTokenService _jwt = Substitute.For<IJwtTokenService>();
    private readonly ICurrentUserService _currentUser = Substitute.For<ICurrentUserService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public CompletePasswordResetHandlerTests()
    {
        _currentUser.UserId.Returns(UserId);
        _hasher.Hash(Arg.Any<string>()).Returns("NEW-HASH");
        _jwt.Issue(Arg.Any<UserEntity>()).Returns(new TokenPair("fresh-access", "fresh-refresh"));
    }

    private CompletePasswordResetHandler Sut() => new(
        _users, _resets, _hasher, _jwt, _currentUser, _uow, NullLogger<CompletePasswordResetHandler>.Instance);

    private UserEntity Pending() => new()
    {
        Id = UserId, Email = "u@u.u", DisplayName = "U", IsActive = true,
        PasswordHash = "OLD-HASH", SecurityStamp = "old", MustChangePassword = true,
    };

    [Fact]
    public async Task Sets_the_new_password_clears_the_flag_rotates_the_stamp_and_burns_resets()
    {
        var user = Pending();
        _users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await Sut().Handle(
            new CompletePasswordResetCommand("BrandNew99", "BrandNew99"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.MustChangePassword.Should().BeFalse();
        result.Value!.AccessToken.Should().Be("fresh-access");
        user.PasswordHash.Should().Be("NEW-HASH");
        user.MustChangePassword.Should().BeFalse();
        user.SecurityStamp.Should().NotBe("old");
        await _resets.Received(1).InvalidateActiveForUserAsync(UserId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _users.Received().AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "PasswordResetCompleted"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejected_when_no_reset_is_pending()
    {
        var user = Pending();
        user.MustChangePassword = false;
        _users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await Sut().Handle(
            new CompletePasswordResetCommand("BrandNew99", "BrandNew99"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_reusing_the_previous_password()
    {
        var user = Pending();
        _users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("BrandNew99", "OLD-HASH").Returns(true);

        var result = await Sut().Handle(
            new CompletePasswordResetCommand("BrandNew99", "BrandNew99"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "newPassword");
    }
}
