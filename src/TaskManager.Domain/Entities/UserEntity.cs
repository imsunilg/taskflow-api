using TaskManager.Domain.Common;
using TaskManager.Domain.Enums;

namespace TaskManager.Domain.Entities;

public class UserEntity : AuditableEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>Deactivated accounts cannot log in and existing sessions are rejected.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime? DeactivatedAt { get; set; }
    public Guid? DeactivatedByUserId { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>Rotated on every password change / forced logout. Baked into access + refresh tokens;
    /// a mismatch invalidates the token, so other sessions die the moment it changes.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>Set after a one-time-password reset login; blocks every protected endpoint
    /// except the forced change-password flow until the user sets a new password.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Soft delete. Deleted accounts are hidden, cannot log in and existing sessions are rejected.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedByUserId { get; set; }

    public ICollection<TaskEntity> Tasks { get; set; } = new List<TaskEntity>();
    public ICollection<CategoryEntity> Categories { get; set; } = new List<CategoryEntity>();
    public ICollection<TagEntity> Tags { get; set; } = new List<TagEntity>();
}

/// <summary>Audit trail for admin-driven account changes (create, update, role change, activate/deactivate, password reset, delete).</summary>
public class UserStatusChangeEntity : AuditableEntity
{
    public Guid TargetUserId { get; set; }
    public Guid ChangedByUserId { get; set; }

    /// <summary>Created | Updated | RoleChanged | Activated | Deactivated | PasswordReset | Deleted</summary>
    public string Action { get; set; } = "StatusChanged";
    public bool PreviousIsActive { get; set; }
    public bool NewIsActive { get; set; }

    /// <summary>Optional human-readable context, e.g. "role: User -> Admin".</summary>
    public string? Details { get; set; }
    public string? Reason { get; set; }
    public DateTime ChangedAt { get; set; }
}

/// <summary>A single-use, short-lived one-time password issued by the "forgot password" flow.
/// Only a hash of the OTP is stored — never the raw value.</summary>
public class PasswordResetEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public string OtpHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? RequestIp { get; set; }
    public int AttemptCount { get; set; }

    public bool IsUsable(DateTime nowUtc) => UsedAt is null && ExpiresAt > nowUtc;
}
