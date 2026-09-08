namespace TaskManager.Domain.Entities;

public class TranscriptionEntity
{
    public Guid Id { get; set; }
    public Guid VoiceRecordingId { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ExtractedJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }

    public VoiceRecordingEntity? VoiceRecording { get; set; }
}
