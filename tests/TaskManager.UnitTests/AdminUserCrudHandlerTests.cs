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

public class AdminUserCrudHandlerTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ICurrentUserService _currentUser = Substitute.For<ICurrentUserService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public AdminUserCrudHandlerTests()
    {
        _currentUser.UserId.Returns(AdminId);
        _hasher.Hash(Arg.Any<string>()).Returns("PBKDF2-HASH");
        _users.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(3);
    }

    private CreateUserCommandHandler CreateSut() =>
        new(_users, _hasher, _currentUser, _uow, NullLogger<CreateUserCommandHandler>.Instance);

    private UpdateUserCommandHandler UpdateSut() =>
        new(_users, _currentUser, _uow, NullLogger<UpdateUserCommandHandler>.Instance);

    private ResetUserPasswordCommandHandler ResetSut() =>
        new(_users, _hasher, _currentUser, _uow, NullLogger<ResetUserPasswordCommandHandler>.Instance);

    private DeleteUserCommandHandler DeleteSut() =>
        new(_users, _currentUser, _uow, NullLogger<DeleteUserCommandHandler>.Instance);

    private void Have(UserEntity u) => _users.GetByIdAsync(u.Id, Arg.Any<CancellationToken>()).Returns(u);

    [Fact]
    public async Task Creates_a_user_with_a_hashed_password_and_an_audit_record()
    {
        UserEntity? added = null;
        _users.When(x => x.AddAsync(Arg.Any<UserEntity>(), Arg.Any<CancellationToken>()))
            .Do(ci => added = ci.Arg<UserEntity>());

        var result = await CreateSut().Handle(
            new CreateUserCommand(new CreateUserRequest("Jane Doe", "Jane@Example.com", "Admin", "Str0ngPass", "Str0ngPass", true)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        added!.Email.Should().Be("jane@example.com");
        added.PasswordHash.Should().Be("PBKDF2-HASH");
        added.Role.Should().Be(UserRole.Admin);
        await _users.Received(1).AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "Created" && c.ChangedByUserId == AdminId),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_fails_when_the_email_is_already_taken()
    {
        _users.EmailExistsAsync("jane@example.com", null, Arg.Any<CancellationToken>()).Returns(true);

        var result = await CreateSut().Handle(
            new CreateUserCommand(new CreateUserRequest("Jane", "Jane@Example.com", "User", "Str0ngPass", "Str0ngPass", true)),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "email");
        await _uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_cannot_demote_the_last_active_admin()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.Admin, IsActive = true, Email = "a@a.a" };
        Have(target);
        _users.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var result = await UpdateSut().Handle(
            new UpdateUserCommand(target.Id, new UpdateUserRequest("A", "a@a.a", "User", true)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("last active admin");
    }

    [Fact]
    public async Task Update_cannot_change_your_own_role()
    {
        var me = new UserEntity { Id = AdminId, Role = UserRole.Admin, IsActive = true, Email = "me@x.com" };
        Have(me);

        var result = await UpdateSut().Handle(
            new UpdateUserCommand(AdminId, new UpdateUserRequest("Me", "me@x.com", "User", true)), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("your own role");
    }

    [Fact]
    public async Task Update_records_a_role_change_audit_row()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.User, IsActive = true, Email = "u@u.u" };
        Have(target);

        var result = await UpdateSut().Handle(
            new UpdateUserCommand(target.Id, new UpdateUserRequest("U", "u@u.u", "Admin", true)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        target.Role.Should().Be(UserRole.Admin);
        await _users.Received().AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "RoleChanged"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_password_hashes_and_audits_without_exposing_the_password()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.User, IsActive = true, Email = "u@u.u" };
        Have(target);

        var result = await ResetSut().Handle(
            new ResetUserPasswordCommand(target.Id, new ResetPasswordRequest("Br4ndNewPass", "Br4ndNewPass")),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        target.PasswordHash.Should().Be("PBKDF2-HASH");
        await _users.Received().AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "PasswordReset"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_cannot_target_your_own_account()
    {
        var me = new UserEntity { Id = AdminId, Role = UserRole.Admin, IsActive = true };
        Have(me);

        var result = await DeleteSut().Handle(new DeleteUserCommand(AdminId), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("your own account");
    }

    [Fact]
    public async Task Delete_cannot_target_the_last_active_admin()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.Admin, IsActive = true };
        Have(target);
        _users.CountActiveAdminsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var result = await DeleteSut().Handle(new DeleteUserCommand(target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("last active admin");
    }

    [Fact]
    public async Task Delete_soft_deletes_the_user_and_writes_an_audit_row()
    {
        var target = new UserEntity { Id = Guid.NewGuid(), Role = UserRole.User, IsActive = true };
        Have(target);

        var result = await DeleteSut().Handle(new DeleteUserCommand(target.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        target.IsDeleted.Should().BeTrue();
        target.IsActive.Should().BeFalse();
        target.DeletedByUserId.Should().Be(AdminId);
        target.DeletedAt.Should().NotBeNull();
        await _users.Received().AddStatusChangeAsync(
            Arg.Is<UserStatusChangeEntity>(c => c.Action == "Deleted" && c.PreviousIsActive && !c.NewIsActive),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }
}
