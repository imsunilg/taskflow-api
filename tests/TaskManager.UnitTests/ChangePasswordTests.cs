using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskManager.Application.Auth;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using Xunit;

namespace TaskManager.UnitTests;

public class ChangePasswordValidatorTests
{
    private readonly ChangePasswordCommandValidator _validator = new();

    private bool Fails(string current, string next, string confirm, string property) =>
        _validator.Validate(new ChangePasswordCommand(current, next, confirm))
            .Errors.Any(e => e.PropertyName == property);

    [Fact]
    public void Accepts_a_strong_distinct_matching_password()
    {
        _validator.Validate(new ChangePasswordCommand("OldPass123", "BrandNew99", "BrandNew99"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_mismatched_confirmation()
    {
        Fails("OldPass123", "BrandNew99", "different99", nameof(ChangePasswordCommand.ConfirmNewPassword))
            .Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_new_password_equal_to_the_current_one()
    {
        Fails("OldPass123", "OldPass123", "OldPass123", nameof(ChangePasswordCommand.NewPassword))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("short1A")]       // too short
    [InlineData("alllowercase1")] // no uppercase
    [InlineData("NoDigitsHere")]  // no digit
    public void Rejects_passwords_that_break_the_policy(string weak)
    {
        Fails("OldPass123", weak, weak, nameof(ChangePasswordCommand.NewPassword)).Should().BeTrue();
    }
}

public class ChangePasswordHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IJwtTokenService _jwt = Substitute.For<IJwtTokenService>();
    private readonly ICurrentUserService _currentUser = Substitute.For<ICurrentUserService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public ChangePasswordHandlerTests()
    {
        _currentUser.UserId.Returns(UserId);
        _hasher.Verify("CorrectCurrent1", "OLD-HASH").Returns(true);
        _hasher.Hash(Arg.Any<string>()).Returns("NEW-HASH");
        _jwt.Issue(Arg.Any<UserEntity>()).Returns(new TokenPair("fresh-access", "fresh-refresh"));
    }

    private ChangePasswordHandler Sut() => new(
        _users, _hasher, _jwt, _currentUser, _uow, NullLogger<ChangePasswordHandler>.Instance);

    private UserEntity Existing() => new()
    {
        Id = UserId, Email = "u@u.u", DisplayName = "U", PasswordHash = "OLD-HASH",
        IsActive = true, SecurityStamp = "old-stamp",
    };

    [Fact]
    public async Task Changes_the_password_rotates_the_stamp_and_keeps_the_session()
    {
        var user = Existing();
        _users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await Sut().Handle(
            new ChangePasswordCommand("CorrectCurrent1", "BrandNew99", "BrandNew99"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Be("fresh-access");
        user.PasswordHash.Should().Be("NEW-HASH");
        user.PasswordChangedAt.Should().NotBeNull();
        user.SecurityStamp.Should().NotBe("old-stamp");
        await _users.Received(1).AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "PasswordChanged" && c.TargetUserId == UserId),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_an_incorrect_current_password_without_persisting()
    {
        var user = Existing();
        _users.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await Sut().Handle(
            new ChangePasswordCommand("WrongCurrent1", "BrandNew99", "BrandNew99"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("Current password is incorrect.");
        result.Errors.Should().Contain(e => e.Field == "currentPassword");
        user.PasswordHash.Should().Be("OLD-HASH");
        user.SecurityStamp.Should().Be("old-stamp");
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fails_when_the_principal_has_no_user_id()
    {
        _currentUser.UserId.Returns(Guid.Empty);

        var result = await Sut().Handle(
            new ChangePasswordCommand("CorrectCurrent1", "BrandNew99", "BrandNew99"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _users.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
