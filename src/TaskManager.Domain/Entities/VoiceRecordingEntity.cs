namespace TaskManager.Domain.Entities;

public class VoiceRecordingEntity
{
    public Guid Id { get; set; }
    public Guid? TaskId { get; set; }
    public Guid UserId { get; set; }
    public string? AudioUrl { get; set; }
    public int DurationSeconds { get; set; }
    public DateTime CreatedAt { get; set; }

    public TaskEntity? Task { get; set; }
    public TranscriptionEntity? Transcription { get; set; }
}
