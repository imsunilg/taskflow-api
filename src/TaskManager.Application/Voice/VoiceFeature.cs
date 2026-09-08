using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Voice;

public record ExtractedFieldsDto(
    string? Title,
    DateOnly? DueDate,
    TimeOnly? DueTime,
    string? Priority,
    int? ReminderOffsetMinutes);

public record TranscriptionResultDto(string RawText, ExtractedFieldsDto ExtractedFields);

/// <summary>
/// Handles both an uploaded audio stream and a text-only re-extract path
/// (exactly one of <see cref="Audio"/> / <see cref="Text"/> is set).
/// </summary>
public record TranscribeAudioCommand(
    Stream? Audio,
    string? ContentType,
    long Length,
    string? Text) : IRequest<Result<TranscriptionResultDto>>;

public class TranscribeAudioCommandHandler : IRequestHandler<TranscribeAudioCommand, Result<TranscriptionResultDto>>
{
    private static readonly string[] AllowedMime = { "audio/webm", "audio/mp4", "audio/wav", "audio/x-wav" };
    private const long MaxBytes = 10 * 1024 * 1024;

    private readonly ISpeechToTextService _stt;
    private readonly ITaskFieldExtractor _extractor;
    private readonly IVoiceRecordingRepository _recordings;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<TranscribeAudioCommandHandler> _logger;

    public TranscribeAudioCommandHandler(
        ISpeechToTextService stt,
        ITaskFieldExtractor extractor,
        IVoiceRecordingRepository recordings,
        ICurrentUserService currentUser,
        IUnitOfWork uow,
        ILogger<TranscribeAudioCommandHandler> logger)
    {
        _stt = stt;
        _extractor = extractor;
        _recordings = recordings;
        _currentUser = currentUser;
        _uow = uow;
        _logger = logger;
    }

    public async Task<Result<TranscriptionResultDto>> Handle(TranscribeAudioCommand c, CancellationToken ct)
    {
        string rawText;
        string provider;

        if (!string.IsNullOrWhiteSpace(c.Text))
        {
            rawText = c.Text.Trim();
            provider = "text";
        }
        else if (c.Audio is not null)
        {
            if (c.Length <= 0)
            {
                return Result<TranscriptionResultDto>.Failure("The uploaded recording was empty.",
                    new[] { new ValidationError("audio", "No audio data was received.") });
            }
            if (c.Length > MaxBytes)
            {
                return Result<TranscriptionResultDto>.Failure("Audio file exceeds the 10MB limit.",
                    new[] { new ValidationError("audio", "Maximum size is 10MB.") });
            }
            if (c.ContentType is null || !AllowedMime.Contains(c.ContentType.Split(';')[0].Trim()))
            {
                return Result<TranscriptionResultDto>.Failure("Unsupported audio format.",
                    new[] { new ValidationError("audio", "Allowed types: webm, mp4, wav.") });
            }

            _logger.LogInformation(
                "Voice transcription request: {Bytes} bytes, content-type {ContentType}", c.Length, c.ContentType);

            // A SpeechToTextException here propagates to VoiceController, which maps it
            // to 502/503 with a typed code — it must never become a fabricated transcript.
            var stt = await _stt.TranscribeAsync(c.Audio, ct);

            rawText = stt.Transcript;
            provider = stt.Provider;

            if (string.IsNullOrWhiteSpace(rawText))
            {
                _logger.LogInformation("Transcription provider {Provider} returned an empty transcript.", provider);
                return Result<TranscriptionResultDto>.Failure(
                    "No speech was recognised in the recording.",
                    new[] { new ValidationError("audio", "The transcript came back empty.") });
            }

            var recording = new VoiceRecordingEntity
            {
                UserId = _currentUser.UserId,
                DurationSeconds = 0,
            };
            var extractedNow = _extractor.Extract(rawText);
            recording.Transcription = new TranscriptionEntity
            {
                RawText = rawText,
                Provider = provider,
                ExtractedJson = JsonSerializer.Serialize(extractedNow),
            };
            await _recordings.AddAsync(recording, ct);
            await _uow.CommitAsync(ct);
        }
        else
        {
            return Result<TranscriptionResultDto>.Failure("No audio or text supplied.");
        }

        var fields = _extractor.Extract(rawText);
        var dto = new TranscriptionResultDto(
            rawText,
            new ExtractedFieldsDto(
                fields.Title,
                fields.DueDate,
                fields.DueTime,
                fields.Priority?.ToString(),
                fields.ReminderOffsetMinutes));

        return Result<TranscriptionResultDto>.Success(dto, "Transcription complete");
    }
}
