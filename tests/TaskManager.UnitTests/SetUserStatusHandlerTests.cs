using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskManager.Application.Admin;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;
using Xunit;

namespace TaskManager.UnitTests;

public class SetUserStatusHandlerTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICurrentUserService _currentUser = Substitute.For<ICurrentUserService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private SetUserStatusCommandHandler Sut() => new(
        _users, _currentUser, _uow, NullLogger<SetUserStatusCommandHandler>.Instance);

    public SetUserStatusHandlerTests()
    {
        _currentUser.UserId.Returns(AdminId);
        _users.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(5);
    }

    private void Have(UserEntity user) =>
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

    [Fact]
    public async Task Rejects_deactivating_your_own_account()
    {
        Have(new UserEntity { Id = AdminId, Role = UserRole.Admin, IsActive = true });

        var result = await Sut().Handle(new SetUserStatusCommand(AdminId, false, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("your own account");
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejects_deactivating_the_last_active_admin()
    {
        var other = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.Admin, IsActive = true };
        Have(other);
        _users.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var result = await Sut().Handle(new SetUserStatusCommand(other.Id, false, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("last active admin");
    }

    [Fact]
    public async Task Unknown_user_fails()
    {
        _users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((UserEntity?)null);

        var result = await Sut().Handle(new SetUserStatusCommand(Guid.NewGuid(), false, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task Deactivation_updates_the_user_and_writes_an_audit_record()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.User, IsActive = true };
        Have(target);

        var result = await Sut().Handle(
            new SetUserStatusCommand(target.Id, false, "policy violation"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsActive.Should().BeFalse();
        target.IsActive.Should().BeFalse();
        target.DeactivatedByUserId.Should().Be(AdminId);
        target.DeactivatedAt.Should().NotBeNull();

        await _users.Received(1).AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c =>
                c.TargetUserId == target.Id &&
                c.ChangedByUserId == AdminId &&
                c.PreviousIsActive && !c.NewIsActive &&
                c.Reason == "policy violation"),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reactivation_clears_the_deactivation_metadata()
    {
        var target = new UserEntity
        {
            Id = Guid.NewGuid(), Role = UserRole.User, IsActive = false,
            DeactivatedAt = DateTime.UtcNow, DeactivatedByUserId = Guid.NewGuid(),
        };
        Have(target);

        var result = await Sut().Handle(new SetUserStatusCommand(target.Id, true, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        target.IsActive.Should().BeTrue();
        target.DeactivatedAt.Should().BeNull();
        target.DeactivatedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task No_op_when_the_status_is_unchanged()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.User, IsActive = true };
        Have(target);

        var result = await Sut().Handle(new SetUserStatusCommand(target.Id, true, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _users.DidNotReceive().AddStatusChangeAsync(Arg.Any<UserStatusChangeEntity>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }
}
