using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;
using TaskManager.Domain.Enums;

namespace TaskManager.Application.Admin;

/// <summary>Safe, secret-free projection of a user for the admin console.</summary>
public record AdminUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    DateTime? DeactivatedAt,
    bool IsDeleted,
    DateTime? DeletedAt);

public record AdminUserSummaryDto(int TotalUsers, int ActiveUsers, int DeactivatedUsers, int NewUsersLast7Days);

/// <summary>Repository-facing filter (already validated / clamped).</summary>
public record AdminUserFilter(string? Search, string? Status, string Sort, bool Desc, int Page, int PageSize, bool IncludeDeleted);

// ---- requests coming off the wire ----
public record CreateUserRequest(string DisplayName, string Email, string Role, string Password, string ConfirmPassword, bool IsActive);
public record UpdateUserRequest(string DisplayName, string Email, string Role, bool IsActive);
public record ResetPasswordRequest(string NewPassword, string ConfirmPassword);

// ---- queries / commands ----
public record GetAdminUsersQuery(
    string? Search,
    string? Status,
    string? Sort,
    bool Desc,
    int Page,
    int PageSize,
    bool IncludeDeleted = false) : IRequest<Result<PagedResult<AdminUserDto>>>;

public record GetAdminUserSummaryQuery : IRequest<Result<AdminUserSummaryDto>>;

public record GetAdminUserQuery(Guid Id) : IRequest<Result<AdminUserDto>>;

public record SetUserStatusCommand(Guid TargetUserId, bool IsActive, string? Reason)
    : IRequest<Result<AdminUserDto>>;

public record CreateUserCommand(CreateUserRequest Data) : IRequest<Result<AdminUserDto>>;
public record UpdateUserCommand(Guid Id, UpdateUserRequest Data) : IRequest<Result<AdminUserDto>>;
public record ResetUserPasswordCommand(Guid Id, ResetPasswordRequest Data) : IRequest<Result<AdminUserDto>>;
public record DeleteUserCommand(Guid Id) : IRequest<Result<AdminUserDto>>;

public static class AdminUserRules
{
    // At least 8 chars, one lowercase, one uppercase, one digit.
    public static readonly Regex Password = new(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,128}$", RegexOptions.Compiled);
    public const string PasswordMessage =
        "Password must be 8-128 characters and include an uppercase letter, a lowercase letter and a number.";

    public static bool TryParseRole(string? value, out UserRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);
}

public class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Data.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Data.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Data.Role).Must(r => AdminUserRules.TryParseRole(r, out _)).WithMessage("Role must be 'User' or 'Admin'.");
        RuleFor(x => x.Data.Password).Must(p => p is not null && AdminUserRules.Password.IsMatch(p)).WithMessage(AdminUserRules.PasswordMessage);
        RuleFor(x => x.Data.ConfirmPassword).Equal(x => x.Data.Password).WithMessage("Passwords do not match.");
    }
}

public class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserCommandValidator()
    {
        RuleFor(x => x.Data.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Data.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Data.Role).Must(r => AdminUserRules.TryParseRole(r, out _)).WithMessage("Role must be 'User' or 'Admin'.");
    }
}

public class ResetUserPasswordCommandValidator : AbstractValidator<ResetUserPasswordCommand>
{
    public ResetUserPasswordCommandValidator()
    {
        RuleFor(x => x.Data.NewPassword).Must(p => p is not null && AdminUserRules.Password.IsMatch(p)).WithMessage(AdminUserRules.PasswordMessage);
        RuleFor(x => x.Data.ConfirmPassword).Equal(x => x.Data.NewPassword).WithMessage("Passwords do not match.");
    }
}

public static class AdminUserMapping
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
        { "createdAt", "displayName", "email", "role", "lastLoginAt", "status" };

    public static AdminUserDto ToDto(this UserEntity u) => new(
        u.Id, u.Email, u.DisplayName, u.Role.ToString(), u.IsActive, u.CreatedAt, u.LastLoginAt, u.DeactivatedAt,
        u.IsDeleted, u.DeletedAt);

    public static AdminUserFilter ToFilter(this GetAdminUsersQuery q)
    {
        var sort = q.Sort is not null && AllowedSorts.Contains(q.Sort) ? q.Sort : "createdAt";
        var page = q.Page < 1 ? 1 : q.Page;
        var pageSize = q.PageSize switch { < 1 => 20, > 100 => 100, _ => q.PageSize };
        var status = q.Status?.Trim().ToLowerInvariant() is "active" or "deactivated" ? q.Status!.Trim().ToLowerInvariant() : "all";
        return new AdminUserFilter(q.Search?.Trim(), status, sort, q.Desc, page, pageSize, q.IncludeDeleted);
    }
}

public class GetAdminUsersQueryHandler : IRequestHandler<GetAdminUsersQuery, Result<PagedResult<AdminUserDto>>>
{
    private readonly IUserRepository _users;
    public GetAdminUsersQueryHandler(IUserRepository users) => _users = users;

    public async Task<Result<PagedResult<AdminUserDto>>> Handle(GetAdminUsersQuery q, CancellationToken ct)
    {
        var page = await _users.GetPagedAsync(q.ToFilter(), ct);
        var mapped = new PagedResult<AdminUserDto>(
            page.Items.Select(u => u.ToDto()).ToList(), page.Page, page.PageSize, page.Total);
        return Result<PagedResult<AdminUserDto>>.Success(mapped);
    }
}

public class GetAdminUserSummaryQueryHandler : IRequestHandler<GetAdminUserSummaryQuery, Result<AdminUserSummaryDto>>
{
    private readonly IUserRepository _users;
    public GetAdminUserSummaryQueryHandler(IUserRepository users) => _users = users;

    public async Task<Result<AdminUserSummaryDto>> Handle(GetAdminUserSummaryQuery _, CancellationToken ct)
    {
        var (total, active, deactivated, newLast7) = await _users.GetStatsAsync(DateTime.UtcNow.AddDays(-7), ct);
        return Result<AdminUserSummaryDto>.Success(new AdminUserSummaryDto(total, active, deactivated, newLast7));
    }
}

public class GetAdminUserQueryHandler : IRequestHandler<GetAdminUserQuery, Result<AdminUserDto>>
{
    private readonly IUserRepository _users;
    public GetAdminUserQueryHandler(IUserRepository users) => _users = users;

    public async Task<Result<AdminUserDto>> Handle(GetAdminUserQuery q, CancellationToken ct)
    {
        var user = await _users.GetByIdIncludingDeletedAsync(q.Id, ct);
        return user is null
            ? Result<AdminUserDto>.Failure("User not found.")
            : Result<AdminUserDto>.Success(user.ToDto());
    }
}

public class SetUserStatusCommandHandler : IRequestHandler<SetUserStatusCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _users;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<SetUserStatusCommandHandler> _logger;

    public SetUserStatusCommandHandler(
        IUserRepository users,
        ICurrentUserService currentUser,
        IUnitOfWork uow,
        ILogger<SetUserStatusCommandHandler> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(SetUserStatusCommand cmd, CancellationToken ct)
    {
        var actingAdminId = _currentUser.UserId;
        var target = await _users.GetByIdAsync(cmd.TargetUserId, ct);
        if (target is null)
        {
            return Result<AdminUserDto>.Failure("User not found.");
        }

        if (!cmd.IsActive && cmd.TargetUserId == actingAdminId)
        {
            return Result<AdminUserDto>.Failure("You cannot deactivate your own account.");
        }

        if (!cmd.IsActive && target.IsActive && target.Role == UserRole.Admin)
        {
            var activeAdmins = await _users.CountActiveAdminsAsync(ct);
            if (activeAdmins <= 1)
            {
                return Result<AdminUserDto>.Failure("The last active admin account cannot be deactivated.");
            }
        }

        if (target.IsActive == cmd.IsActive)
        {
            return Result<AdminUserDto>.Success(target.ToDto(), "No change.");
        }

        var now = DateTime.UtcNow;
        var previous = target.IsActive;
        target.IsActive = cmd.IsActive;
        target.DeactivatedAt = cmd.IsActive ? null : now;
        target.DeactivatedByUserId = cmd.IsActive ? null : actingAdminId;

        await _users.AddStatusChangeAsync(new UserStatusChangeEntity
        {
            TargetUserId = target.Id,
            ChangedByUserId = actingAdminId,
            Action = cmd.IsActive ? "Activated" : "Deactivated",
            PreviousIsActive = previous,
            NewIsActive = cmd.IsActive,
            Reason = string.IsNullOrWhiteSpace(cmd.Reason) ? null : cmd.Reason!.Trim(),
            ChangedAt = now,
        }, ct);

        await _uow.CommitAsync(ct);

        _logger.LogInformation(
            "Admin {Admin} set user {Target} active={Active}", actingAdminId, target.Id, cmd.IsActive);

        return Result<AdminUserDto>.Success(
            target.ToDto(), cmd.IsActive ? "User activated." : "User deactivated.");
    }
}

public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<CreateUserCommandHandler> _logger;

    public CreateUserCommandHandler(
        IUserRepository users, IPasswordHasher hasher, ICurrentUserService currentUser,
        IUnitOfWork uow, ILogger<CreateUserCommandHandler> logger)
    {
        _users = users;
        _hasher = hasher;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(CreateUserCommand cmd, CancellationToken ct)
    {
        var email = cmd.Data.Email.Trim().ToLowerInvariant();
        if (await _users.EmailExistsAsync(email, null, ct))
        {
            return Result<AdminUserDto>.Failure(
                "A user with this email already exists.",
                new[] { new ValidationError("email", "This email is already in use.") });
        }

        AdminUserRules.TryParseRole(cmd.Data.Role, out var role);

        var user = new UserEntity
        {
            Email = email,
            DisplayName = cmd.Data.DisplayName.Trim(),
            PasswordHash = _hasher.Hash(cmd.Data.Password),
            Role = role,
            IsActive = cmd.Data.IsActive,
        };
        await _users.AddAsync(user, ct);
        await _users.AddStatusChangeAsync(new UserStatusChangeEntity
        {
            TargetUserId = user.Id,
            ChangedByUserId = _currentUser.UserId,
            Action = "Created",
            PreviousIsActive = false,
            NewIsActive = user.IsActive,
            Details = $"role: {role}",
            ChangedAt = DateTime.UtcNow,
        }, ct);
        await _uow.CommitAsync(ct);

        _logger.LogInformation("Admin {Admin} created user {Target} ({Email})", _currentUser.UserId, user.Id, email);
        return Result<AdminUserDto>.Success(user.ToDto(), "User created.");
    }
}

public class UpdateUserCommandHandler : IRequestHandler<UpdateUserCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _users;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<UpdateUserCommandHandler> _logger;

    public UpdateUserCommandHandler(
        IUserRepository users, ICurrentUserService currentUser, IUnitOfWork uow, ILogger<UpdateUserCommandHandler> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(UpdateUserCommand cmd, CancellationToken ct)
    {
        var acting = _currentUser.UserId;
        var target = await _users.GetByIdAsync(cmd.Id, ct);
        if (target is null)
        {
            return Result<AdminUserDto>.Failure("User not found.");
        }

        var email = cmd.Data.Email.Trim().ToLowerInvariant();
        if (email != target.Email && await _users.EmailExistsAsync(email, target.Id, ct))
        {
            return Result<AdminUserDto>.Failure(
                "A user with this email already exists.",
                new[] { new ValidationError("email", "This email is already in use.") });
        }

        AdminUserRules.TryParseRole(cmd.Data.Role, out var newRole);
        var roleChanged = newRole != target.Role;
        var deactivating = target.IsActive && !cmd.Data.IsActive;
        var activating = !target.IsActive && cmd.Data.IsActive;

        if (target.Id == acting && (roleChanged || deactivating))
        {
            return Result<AdminUserDto>.Failure("You cannot change your own role or deactivate your own account.");
        }

        if (target.Role == UserRole.Admin && target.IsActive && (roleChanged || deactivating))
        {
            var activeAdmins = await _users.CountActiveAdminsAsync(ct);
            if (activeAdmins <= 1)
            {
                return Result<AdminUserDto>.Failure("The last active admin account cannot be demoted or deactivated.");
            }
        }

        var now = DateTime.UtcNow;
        var previousActive = target.IsActive;
        var previousRole = target.Role;

        target.DisplayName = cmd.Data.DisplayName.Trim();
        target.Email = email;
        target.Role = newRole;
        target.IsActive = cmd.Data.IsActive;
        if (deactivating)
        {
            target.DeactivatedAt = now;
            target.DeactivatedByUserId = acting;
        }
        else if (activating)
        {
            target.DeactivatedAt = null;
            target.DeactivatedByUserId = null;
        }

        if (roleChanged)
        {
            await _users.AddStatusChangeAsync(Audit(target, acting, "RoleChanged", previousActive, target.IsActive,
                $"role: {previousRole} -> {newRole}", now), ct);
        }
        if (deactivating || activating)
        {
            await _users.AddStatusChangeAsync(Audit(target, acting, deactivating ? "Deactivated" : "Activated",
                previousActive, target.IsActive, null, now), ct);
        }
        if (!roleChanged && !deactivating && !activating)
        {
            await _users.AddStatusChangeAsync(Audit(target, acting, "Updated", previousActive, target.IsActive, "profile", now), ct);
        }

        await _uow.CommitAsync(ct);
        _logger.LogInformation("Admin {Admin} updated user {Target}", acting, target.Id);
        return Result<AdminUserDto>.Success(target.ToDto(), "User updated.");
    }

    private static UserStatusChangeEntity Audit(
        UserEntity target, Guid acting, string action, bool prev, bool now, string? details, DateTime at) => new()
    {
        TargetUserId = target.Id,
        ChangedByUserId = acting,
        Action = action,
        PreviousIsActive = prev,
        NewIsActive = now,
        Details = details,
        ChangedAt = at,
    };
}

public class ResetUserPasswordCommandHandler : IRequestHandler<ResetUserPasswordCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ResetUserPasswordCommandHandler> _logger;

    public ResetUserPasswordCommandHandler(
        IUserRepository users, IPasswordHasher hasher, ICurrentUserService currentUser,
        IUnitOfWork uow, ILogger<ResetUserPasswordCommandHandler> logger)
    {
        _users = users;
        _hasher = hasher;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(ResetUserPasswordCommand cmd, CancellationToken ct)
    {
        var target = await _users.GetByIdAsync(cmd.Id, ct);
        if (target is null)
        {
            return Result<AdminUserDto>.Failure("User not found.");
        }

        target.PasswordHash = _hasher.Hash(cmd.Data.NewPassword);
        await _users.AddStatusChangeAsync(new UserStatusChangeEntity
        {
            TargetUserId = target.Id,
            ChangedByUserId = _currentUser.UserId,
            Action = "PasswordReset",
            PreviousIsActive = target.IsActive,
            NewIsActive = target.IsActive,
            ChangedAt = DateTime.UtcNow,
        }, ct);
        await _uow.CommitAsync(ct);

        _logger.LogInformation("Admin {Admin} reset the password for user {Target}", _currentUser.UserId, target.Id);
        return Result<AdminUserDto>.Success(target.ToDto(), "Password reset. Share the temporary password securely.");
    }
}

public class DeleteUserCommandHandler : IRequestHandler<DeleteUserCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _users;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<DeleteUserCommandHandler> _logger;

    public DeleteUserCommandHandler(
        IUserRepository users, ICurrentUserService currentUser, IUnitOfWork uow, ILogger<DeleteUserCommandHandler> logger)
    {
        _users = users;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(DeleteUserCommand cmd, CancellationToken ct)
    {
        var acting = _currentUser.UserId;
        var target = await _users.GetByIdAsync(cmd.Id, ct);
        if (target is null)
        {
            return Result<AdminUserDto>.Failure("User not found.");
        }

        if (target.Id == acting)
        {
            return Result<AdminUserDto>.Failure("You cannot delete your own account.");
        }

        if (target.Role == UserRole.Admin && target.IsActive)
        {
            var activeAdmins = await _users.CountActiveAdminsAsync(ct);
            if (activeAdmins <= 1)
            {
                return Result<AdminUserDto>.Failure("The last active admin account cannot be deleted.");
            }
        }

        var now = DateTime.UtcNow;
        var previousActive = target.IsActive;
        target.IsDeleted = true;
        target.DeletedAt = now;
        target.DeletedByUserId = acting;
        target.IsActive = false;
        target.DeactivatedAt ??= now;
        target.DeactivatedByUserId ??= acting;

        await _users.AddStatusChangeAsync(new UserStatusChangeEntity
        {
            TargetUserId = target.Id,
            ChangedByUserId = acting,
            Action = "Deleted",
            PreviousIsActive = previousActive,
            NewIsActive = false,
            Details = "soft delete",
            ChangedAt = now,
        }, ct);
        await _uow.CommitAsync(ct);

        _logger.LogInformation("Admin {Admin} soft-deleted user {Target}", acting, target.Id);
        return Result<AdminUserDto>.Success(target.ToDto(), "User deleted.");
    }
}
