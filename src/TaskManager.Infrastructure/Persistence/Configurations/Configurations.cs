using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskManager.Domain.Entities;

namespace TaskManager.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<UserEntity>
{
    public void Configure(EntityTypeBuilder<UserEntity> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Email).HasColumnName("email").IsRequired().HasMaxLength(320);
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
        b.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(120);
        b.Property(x => x.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(TaskManager.Domain.Enums.UserRole.User);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
        b.Property(x => x.DeactivatedByUserId).HasColumnName("deactivated_by_user_id");
        b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
        b.Property(x => x.SecurityStamp).HasColumnName("security_stamp").IsRequired().HasMaxLength(64)
            .HasDefaultValueSql("md5(random()::text || clock_timestamp()::text)");
        b.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at");
        b.Property(x => x.MustChangePassword).HasColumnName("must_change_password").HasDefaultValue(false);
        b.Property(x => x.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedByUserId).HasColumnName("deleted_by_user_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.Email).IsUnique();
        b.HasIndex(x => x.IsActive);
        b.HasQueryFilter(x => !x.IsDeleted);
    }
}

public class UserStatusChangeConfiguration : IEntityTypeConfiguration<UserStatusChangeEntity>
{
    public void Configure(EntityTypeBuilder<UserStatusChangeEntity> b)
    {
        b.ToTable("user_status_changes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TargetUserId).HasColumnName("target_user_id");
        b.Property(x => x.ChangedByUserId).HasColumnName("changed_by_user_id");
        b.Property(x => x.Action).HasColumnName("action").IsRequired().HasMaxLength(40).HasDefaultValue("StatusChanged");
        b.Property(x => x.Details).HasColumnName("details").HasMaxLength(500);
        b.Property(x => x.PreviousIsActive).HasColumnName("previous_is_active");
        b.Property(x => x.NewIsActive).HasColumnName("new_is_active");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
        b.Property(x => x.ChangedAt).HasColumnName("changed_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => x.TargetUserId);
    }
}

public class PasswordResetConfiguration : IEntityTypeConfiguration<PasswordResetEntity>
{
    public void Configure(EntityTypeBuilder<PasswordResetEntity> b)
    {
        b.ToTable("password_resets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.OtpHash).HasColumnName("otp_hash").IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.UsedAt).HasColumnName("used_at");
        b.Property(x => x.RequestIp).HasColumnName("request_ip").HasMaxLength(64);
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasIndex(x => new { x.UserId, x.UsedAt });
        b.HasIndex(x => x.ExpiresAt);
    }
}

public class TaskConfiguration : IEntityTypeConfiguration<TaskEntity>
{
    public void Configure(EntityTypeBuilder<TaskEntity> b)
    {
        b.ToTable("tasks");
        b.HasKey(x => x.Id);
        b.Ignore(x => x.DomainEvents);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Title).HasColumnName("title").IsRequired().HasMaxLength(500);
        b.Property(x => x.Description).HasColumnName("description").HasMaxLength(5000);
        b.Property(x => x.DueDate).HasColumnName("due_date");
        b.Property(x => x.DueTime).HasColumnName("due_time");
        b.Property(x => x.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CategoryId).HasColumnName("category_id");
        b.Property(x => x.Completed).HasColumnName("completed");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.Important).HasColumnName("important");
        b.Property(x => x.Notes).HasColumnName("notes");
        b.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.RecurrenceRuleId).HasColumnName("recurrence_rule_id");
        b.Property(x => x.IsVoiceCreated).HasColumnName("is_voice_created");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");

        b.HasQueryFilter(x => !x.IsDeleted);

        b.HasOne(x => x.User).WithMany(u => u.Tasks).HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Category).WithMany(c => c.Tasks).HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.RecurrenceRule).WithMany().HasForeignKey(x => x.RecurrenceRuleId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Reminder).WithOne(r => r.Task!).HasForeignKey<ReminderEntity>(r => r.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Subtasks).WithOne(s => s.Task!).HasForeignKey(s => s.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.TaskTags).WithOne(tt => tt.Task!).HasForeignKey(tt => tt.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.History).WithOne(h => h.Task!).HasForeignKey(h => h.TaskId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.UserId, x.DueDate });
        b.HasIndex(x => new { x.UserId, x.Completed }).HasFilter("is_deleted = false");
        b.HasIndex(x => new { x.UserId, x.CategoryId });
    }
}

public class SubtaskConfiguration : IEntityTypeConfiguration<SubtaskEntity>
{
    public void Configure(EntityTypeBuilder<SubtaskEntity> b)
    {
        b.ToTable("task_subtasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.Text).HasColumnName("text").IsRequired().HasMaxLength(500);
        b.Property(x => x.IsDone).HasColumnName("is_done");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

public class CategoryConfiguration : IEntityTypeConfiguration<CategoryEntity>
{
    public void Configure(EntityTypeBuilder<CategoryEntity> b)
    {
        b.ToTable("categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
        b.Property(x => x.Color).HasColumnName("color").IsRequired().HasMaxLength(9);
        b.Property(x => x.Icon).HasColumnName("icon").HasMaxLength(50);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne(x => x.User).WithMany(u => u.Categories).HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
    }
}

public class TagConfiguration : IEntityTypeConfiguration<TagEntity>
{
    public void Configure(EntityTypeBuilder<TagEntity> b)
    {
        b.ToTable("tags");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(50);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne(x => x.User).WithMany(u => u.Tags).HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
    }
}

public class TaskTagConfiguration : IEntityTypeConfiguration<TaskTagEntity>
{
    public void Configure(EntityTypeBuilder<TaskTagEntity> b)
    {
        b.ToTable("task_tags");
        b.HasKey(x => new { x.TaskId, x.TagId });
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.TagId).HasColumnName("tag_id");
        b.HasOne(x => x.Tag).WithMany(t => t.TaskTags).HasForeignKey(x => x.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ReminderConfiguration : IEntityTypeConfiguration<ReminderEntity>
{
    public void Configure(EntityTypeBuilder<ReminderEntity> b)
    {
        b.ToTable("reminders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.OffsetMinutes).HasColumnName("offset_minutes");
        b.Property(x => x.FiresAt).HasColumnName("fires_at");
        b.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => x.TaskId).IsUnique();
        b.HasIndex(x => new { x.FiresAt, x.DeliveredAt });
    }
}

public class RecurrenceRuleConfiguration : IEntityTypeConfiguration<RecurrenceRuleEntity>
{
    public void Configure(EntityTypeBuilder<RecurrenceRuleEntity> b)
    {
        b.ToTable("recurrence_rules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Frequency).HasColumnName("frequency").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Interval).HasColumnName("interval");
        b.Property(x => x.DaysOfWeek).HasColumnName("days_of_week");
        b.Property(x => x.DayOfMonth).HasColumnName("day_of_month");
        b.Property(x => x.Until).HasColumnName("until");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<NotificationEntity>
{
    public void Configure(EntityTypeBuilder<NotificationEntity> b)
    {
        b.ToTable("notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Title).HasColumnName("title").IsRequired().HasMaxLength(300);
        b.Property(x => x.ReadAt).HasColumnName("read_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.ReadAt });
    }
}

public class VoiceRecordingConfiguration : IEntityTypeConfiguration<VoiceRecordingEntity>
{
    public void Configure(EntityTypeBuilder<VoiceRecordingEntity> b)
    {
        b.ToTable("voice_recordings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.AudioUrl).HasColumnName("audio_url");
        b.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(x => x.Transcription).WithOne(t => t.VoiceRecording!)
            .HasForeignKey<TranscriptionEntity>(t => t.VoiceRecordingId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TranscriptionConfiguration : IEntityTypeConfiguration<TranscriptionEntity>
{
    public void Configure(EntityTypeBuilder<TranscriptionEntity> b)
    {
        b.ToTable("transcriptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.VoiceRecordingId).HasColumnName("voice_recording_id");
        b.Property(x => x.RawText).HasColumnName("raw_text");
        b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(50);
        b.Property(x => x.ExtractedJson).HasColumnName("extracted_json").HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

public class TaskHistoryConfiguration : IEntityTypeConfiguration<TaskHistoryEntity>
{
    public void Configure(EntityTypeBuilder<TaskHistoryEntity> b)
    {
        b.ToTable("task_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.ChangeText).HasColumnName("change_text").IsRequired().HasMaxLength(500);
        b.Property(x => x.ChangedAt).HasColumnName("changed_at");
        b.Property(x => x.ChangedBy).HasColumnName("changed_by");
    }
}

public class AttachmentConfiguration : IEntityTypeConfiguration<AttachmentEntity>
{
    public void Configure(EntityTypeBuilder<AttachmentEntity> b)
    {
        b.ToTable("attachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.FileUrl).HasColumnName("file_url").IsRequired();
        b.Property(x => x.MimeType).HasColumnName("mime_type").HasMaxLength(100);
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TaskCommentConfiguration : IEntityTypeConfiguration<TaskCommentEntity>
{
    public void Configure(EntityTypeBuilder<TaskCommentEntity> b)
    {
        b.ToTable("task_comments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TaskId).HasColumnName("task_id");
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.AuthorId).HasColumnName("author_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}
