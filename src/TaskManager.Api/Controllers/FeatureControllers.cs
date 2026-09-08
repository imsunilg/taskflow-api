using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskManager.Application.Calendar;
using TaskManager.Application.Categories;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Application.Notifications;
using TaskManager.Application.Reminders;
using TaskManager.Application.Search;
using TaskManager.Application.Tags;
using TaskManager.Application.Voice;
using TaskManager.Infrastructure.Speech;

namespace TaskManager.Api.Controllers;

public class CategoriesController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => FromResult(await Mediator.Send(new GetCategoriesQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new CreateCategoryCommand(body), ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCategoryRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new UpdateCategoryCommand(id, body), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new DeleteCategoryCommand(id), ct));
}

public class TagsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => FromResult(await Mediator.Send(new GetTagsQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTagRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new CreateTagCommand(body), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new DeleteTagCommand(id), ct));
}

public class RemindersController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => FromResult(await Mediator.Send(new GetRemindersQuery(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReminderRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new CreateReminderCommand(body), ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateReminderRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new UpdateReminderCommand(id, body), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new DeleteReminderCommand(id), ct));
}

public class NotificationsController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => FromPaged(await Mediator.Send(new GetNotificationsQuery(page, pageSize), ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new MarkNotificationReadCommand(id), ct));

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
        => FromResult(await Mediator.Send(new MarkAllNotificationsReadCommand(), ct));
}

[Route("api/calendar")]
public class CalendarController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => FromResult(await Mediator.Send(new GetCalendarTasksQuery(from, to), ct));
}

[Route("api/search")]
public class SearchController : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => FromPaged(await Mediator.Send(new SearchTasksQuery(q, page, pageSize), ct));
}

[Route("api/voice")]
public class VoiceController : ApiControllerBase
{
    private readonly VoiceTranscriptionConfig _voice;
    private readonly ILogger<VoiceController> _logger;

    public VoiceController(VoiceTranscriptionConfig voice, ILogger<VoiceController> logger)
    {
        _voice = voice;
        _logger = logger;
    }

    public record VoiceConfigDto(string Mode, bool CloudReady, long MaxAudioBytes, IReadOnlyList<string> AllowedMimeTypes);

    public class TranscribeForm
    {
        /// <summary>Recorded audio (webm/mp4/wav, max 10MB). Omit when re-extracting from text.</summary>
        public IFormFile? Audio { get; set; }

        /// <summary>Edited transcript for the text-only re-extract path. Omit when uploading audio.</summary>
        public string? Text { get; set; }
    }

    /// <summary>
    /// Secret-free description of the active transcription mode so the SPA can pick
    /// the correct capture flow before it starts recording. Safe to call anonymously.
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult GetConfig()
    {
        var cloudReady = _voice.Mode == VoiceTranscriptionMode.Cloud && _voice.CloudConfigured;
        // "Demo" is a dev switch — present it to the client as Cloud that is ready.
        var mode = _voice.Mode switch
        {
            VoiceTranscriptionMode.Cloud => "Cloud",
            VoiceTranscriptionMode.Disabled => "Disabled",
            VoiceTranscriptionMode.Demo => "Cloud",
            _ => "Browser",
        };
        var dto = new VoiceConfigDto(
            mode,
            cloudReady || _voice.Mode == VoiceTranscriptionMode.Demo,
            _voice.MaxAudioBytes,
            _voice.AllowedMimeTypes);
        return Ok(ApiResponse<VoiceConfigDto>.Ok(dto, "Voice configuration"));
    }

    [HttpPost("transcribe")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> Transcribe([FromForm] TranscribeForm form, CancellationToken ct)
    {
        var isAudioUpload = form.Audio is not null && string.IsNullOrWhiteSpace(form.Text);

        // The text-only re-extract path never needs a provider and is always allowed.
        if (isAudioUpload)
        {
            switch (_voice.Mode)
            {
                case VoiceTranscriptionMode.Browser:
                    return Problem(
                        StatusCodes.Status409Conflict, "provider_mode_mismatch",
                        "This environment transcribes speech in the browser — audio is not uploaded here.");
                case VoiceTranscriptionMode.Disabled:
                    return Problem(
                        StatusCodes.Status409Conflict, "provider_disabled",
                        "Voice transcription is turned off in this environment.");
                case VoiceTranscriptionMode.Cloud when !_voice.CloudConfigured:
                    _logger.LogError("Voice upload received but Cloud STT is not configured: {Error}",
                        _voice.ConfigurationError);
                    return Problem(
                        StatusCodes.Status503ServiceUnavailable, "stt_not_configured",
                        "The cloud transcription service is not configured on the server.");
            }
        }

        Stream? stream = form.Audio?.OpenReadStream();
        var command = new TranscribeAudioCommand(
            stream, form.Audio?.ContentType, form.Audio?.Length ?? 0, form.Text);

        try
        {
            var result = await Mediator.Send(command, ct);
            return FromResult(result);
        }
        catch (SpeechToTextException ex)
        {
            _logger.LogError(ex, "Cloud speech-to-text provider call failed.");
            return Problem(
                StatusCodes.Status502BadGateway, "stt_upstream_error",
                "The transcription service could not be reached. Please try again shortly.");
        }
    }

    private IActionResult Problem(int status, string code, string message)
    {
        Response.StatusCode = status;
        return new ObjectResult(ApiResponse<TranscriptionResultDto>.Fail(
            message, new[] { new ApiErrorItem("code", code) }))
        {
            StatusCode = status,
        };
    }
}
