namespace TaskManager.Domain.Entities;

public class AttachmentEntity
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string FileUrl { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }

    public TaskEntity? Task { get; set; }
}
