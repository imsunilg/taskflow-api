using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TaskManager.Application.AI;
using TaskManager.Application.Common;
using TaskManager.Infrastructure.AI;

namespace TaskManager.Api.Controllers;

[Route("api/ai")]
[EnableRateLimiting("ai")]
public class AIController : ApiControllerBase
{
    private readonly GeminiOptions _gemini;
    public AIController(IOptions<GeminiOptions> gemini) => _gemini = gemini.Value;

    public record AiConfigDto(bool Configured, string Model);
    public record AnalyzeTextRequest(string Text);
    public record BreakdownPreviewRequest(Guid TaskId);
    public record AssistantMessageRequest(string Message);
    public record NextTaskRequest(List<Guid>? Dismissed);
    public record SearchRequest(string Query);
    public record VoiceCommandRequest(string Transcript);

    /// <summary>Lets the SPA show a graceful "AI unavailable" state instead of firing a doomed request.</summary>
    [HttpGet("config")]
    public IActionResult GetConfig() =>
        Ok(ApiResponse<AiConfigDto>.Ok(new AiConfigDto(_gemini.IsConfigured, _gemini.Model), "AI configuration"));

    // ---- Smart Quick Add + Task Analyzer -------------------------------------------------

    [HttpPost("tasks/analyze")]
    public async Task<IActionResult> AnalyzeText([FromBody] AnalyzeTextRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new AnalyzeTaskTextCommand(body.Text), ct));

    [HttpGet("tasks/{id:guid}/analysis")]
    public async Task<IActionResult> GetAnalysis(Guid id, CancellationToken ct)
        => FromResult(await Mediator.Send(new GetTaskAnalysisQuery(id), ct));

    // ---- Task Breakdown -------------------------------------------------------------------

    [HttpPost("tasks/breakdown")]
    public async Task<IActionResult> PreviewBreakdown([FromBody] BreakdownPreviewRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new PreviewBreakdownCommand(body.TaskId), ct));

    [HttpPost("tasks/{id:guid}/breakdown/confirm")]
    public async Task<IActionResult> ConfirmBreakdown(Guid id, [FromBody] ConfirmBreakdownRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new ConfirmBreakdownCommand(id, body), ct));

    // ---- Daily Planner + Next-task recommendation ------------------------------------------

    [HttpPost("daily-plan")]
    public async Task<IActionResult> GenerateDailyPlan(CancellationToken ct)
        => FromResult(await Mediator.Send(new GenerateDailyPlanCommand(), ct));

    [HttpGet("recommendations/next")]
    public async Task<IActionResult> NextTask([FromQuery] List<Guid>? dismissed, CancellationToken ct)
        => FromResult(await Mediator.Send(new GetNextTaskQuery(dismissed), ct));

    // ---- Daily Summary ---------------------------------------------------------------------

    [HttpGet("daily-summary")]
    public async Task<IActionResult> DailySummary(CancellationToken ct)
        => FromResult(await Mediator.Send(new GetDailySummaryQuery(), ct));

    // ---- Natural-language search -----------------------------------------------------------

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new NLSearchCommand(body.Query), ct));

    // ---- AI Assistant (function calling) -----------------------------------------------------

    [HttpPost("assistant")]
    public async Task<IActionResult> Assistant([FromBody] AssistantMessageRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new ProcessAssistantMessageCommand(body.Message), ct));

    /// <summary>
    /// The ONLY endpoint that ever mutates data on the AI Assistant's behalf. Re-validates the
    /// tool whitelist and re-resolves task ownership from the arguments supplied — nothing is
    /// trusted just because the client echoes back what /assistant returned as a preview.
    /// </summary>
    [HttpPost("assistant/confirm")]
    public async Task<IActionResult> ConfirmAssistantAction([FromBody] AssistantActionRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new ConfirmAssistantActionCommand(body), ct));

    /// <summary>
    /// Direct reschedule-apply entry point for the Task Detail "Reschedule with AI" UI (as
    /// opposed to going through the chat). Same whitelist + ownership re-validation as
    /// assistant/confirm — this is not a separate, less-checked code path.
    /// </summary>
    [HttpPost("tasks/reschedule")]
    public async Task<IActionResult> Reschedule([FromBody] AssistantActionRequest body, CancellationToken ct)
        => FromResult(await Mediator.Send(new ConfirmAssistantActionCommand(body), ct));

    // ---- Voice routing (plain routing logic — not a new AI call type) -----------------------

    private static readonly System.Text.RegularExpressions.Regex CreationVerb = new(
        @"^\s*(create|add|make|set up|remind me to)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public record VoiceResultDto(string Kind, object Data);

    [HttpPost("voice-command")]
    public async Task<IActionResult> VoiceCommand([FromBody] VoiceCommandRequest body, CancellationToken ct)
    {
        if (CreationVerb.IsMatch(body.Transcript ?? string.Empty))
        {
            var draft = await Mediator.Send(new AnalyzeTaskTextCommand(body.Transcript!), ct);
            return draft.IsSuccess
                ? Ok(ApiResponse<VoiceResultDto>.Ok(new VoiceResultDto("create_task", draft.Value!), "Understood as a new task"))
                : BadRequest(ApiResponse<VoiceResultDto>.Fail(draft.Message));
        }

        var turn = await Mediator.Send(new ProcessAssistantMessageCommand(body.Transcript ?? string.Empty), ct);
        return turn.IsSuccess
            ? Ok(ApiResponse<VoiceResultDto>.Ok(new VoiceResultDto("assistant", turn.Value!), "Understood"))
            : BadRequest(ApiResponse<VoiceResultDto>.Fail(turn.Message));
    }
}
