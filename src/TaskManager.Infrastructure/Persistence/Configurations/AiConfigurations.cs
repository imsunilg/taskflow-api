using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskManager.Domain.Entities;

namespace TaskManager.Infrastructure.Persistence.Configurations;

public class AiConversationConfiguration : IEntityTypeConfiguration<AiConversationEntity>
{
    public void Configure(EntityTypeBuilder<AiConversationEntity> b)
    {
        b.ToTable("ai_conversations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Title).HasColumnName("title").IsRequired().HasMaxLength(200);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

public class AiMessageConfiguration : IEntityTypeConfiguration<AiMessageEntity>
{
    public void Configure(EntityTypeBuilder<AiMessageEntity> b)
    {
        b.ToTable("ai_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ConversationId).HasColumnName("conversation_id");
        b.Property(x => x.Role).HasColumnName("role").IsRequired().HasMaxLength(20);
        b.Property(x => x.Content).HasColumnName("content").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne(x => x.Conversation).WithMany(c => c.Messages)
            .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ConversationId, x.CreatedAt });
    }
}

public class AiUsageConfiguration : IEntityTypeConfiguration<AiUsageEntity>
{
    public void Configure(EntityTypeBuilder<AiUsageEntity> b)
    {
        b.ToTable("ai_usage");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.Model).HasColumnName("model").IsRequired().HasMaxLength(50);
        b.Property(x => x.RequestType).HasColumnName("request_type").IsRequired().HasMaxLength(50);
        b.Property(x => x.InputTokens).HasColumnName("input_tokens");
        b.Property(x => x.OutputTokens).HasColumnName("output_tokens");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}

public class AiDailyPlanConfiguration : IEntityTypeConfiguration<AiDailyPlanEntity>
{
    public void Configure(EntityTypeBuilder<AiDailyPlanEntity> b)
    {
        b.ToTable("ai_daily_plans");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.PlanDate).HasColumnName("plan_date");
        b.Property(x => x.Summary).HasColumnName("summary");
        b.Property(x => x.BlocksJson).HasColumnName("blocks_json").HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UserId, x.PlanDate }).IsUnique();
    }
}
