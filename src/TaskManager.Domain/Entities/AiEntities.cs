using TaskManager.Domain.Common;

namespace TaskManager.Domain.Entities;

/// <summary>One AI Assistant conversation thread for a user. Currently one running thread per user.</summary>
public class AiConversationEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public string Title { get; set; } = "AI Assistant";

    public UserEntity? User { get; set; }
    public ICollection<AiMessageEntity> Messages { get; set; } = new List<AiMessageEntity>();
}

/// <summary>A single turn in an AI conversation. Role is "user" or "assistant".</summary>
public class AiMessageEntity : AuditableEntity
{
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;

    public AiConversationEntity? Conversation { get; set; }
}

/// <summary>One Gemini call's token accounting, for daily-quota enforcement and cost visibility.</summary>
public class AiUsageEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}

/// <summary>The most recently generated AI daily plan for a user, one row per user per day.</summary>
public class AiDailyPlanEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public DateOnly PlanDate { get; set; }
    public string Summary { get; set; } = string.Empty;

    /// <summary>Serialized <c>PlanBlockDto[]</c> (JSON) — see TaskManager.Application.AI.DTOs.</summary>
    public string BlocksJson { get; set; } = "[]";
}
