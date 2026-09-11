using System.Text.Json;
using FluentValidation;
using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.AI;

// ---- wire shapes -----------------------------------------------------------

/// <summary>What the client sends back to /assistant/confirm — exactly the tool + args it was shown.</summary>
public record AssistantActionRequest(string ToolName, Dictionary<string, JsonElement> Arguments);

public record AssistantTurnResponse(
    string Reply,
    string? ToolName,
    Dictionary<string, JsonElement>? Arguments,
    bool RequiresConfirmation,
    string? PreviewKind,     // "create_task" | "reschedule" | "generic"
    object? Preview,
    List<TaskSummaryDto>? Results);

// ---- requests ---------------------------------------------------------------

public record ProcessAssistantMessageCommand(string Message) : IRequest<Result<AssistantTurnResponse>>, IAiRequest
{
    public string AiRequestType => "assistant";
}

/// <summary>
/// Applies exactly the tool+args the user confirmed. Re-validates the tool against the whitelist
/// and re-resolves task ownership from scratch — nothing here trusts anything the client sent
/// beyond "which whitelisted tool, with which arguments".
/// </summary>
public record ConfirmAssistantActionCommand(AssistantActionRequest Action) : IRequest<Result<AssistantTurnResponse>>;

public class ProcessAssistantMessageValidator : AbstractValidator<ProcessAssistantMessageCommand>
{
    public ProcessAssistantMessageValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(2000);
    }
}

public class AssistantHandlers :
    IRequestHandler<ProcessAssistantMessageCommand, Result<AssistantTurnResponse>>,
    IRequestHandler<ConfirmAssistantActionCommand, Result<AssistantTurnResponse>>
{
    private readonly IAIService _ai;
    private readonly IAiRepository _aiRepo;
    private readonly ToolDispatcher _dispatcher;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;

    public AssistantHandlers(
        IAIService ai, IAiRepository aiRepo, ToolDispatcher dispatcher, ICurrentUserService currentUser, IUnitOfWork uow)
    {
        _ai = ai;
        _aiRepo = aiRepo;
        _dispatcher = dispatcher;
        _currentUser = currentUser;
        _uow = uow;
    }

    public async Task<Result<AssistantTurnResponse>> Handle(ProcessAssistantMessageCommand command, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var conversation = await _aiRepo.GetOrCreateConversationAsync(userId, ct);
        var recent = await _aiRepo.GetRecentMessagesAsync(conversation.Id, 12, ct);

        await _aiRepo.AddMessageAsync(new AiMessageEntity
        {
            ConversationId = conversation.Id, Role = "user", Content = command.Message,
        }, ct);

        var history = recent.Select(m => new ChatMessageDto(m.Role, m.Content)).ToList();
        var intent = await _ai.ResolveAssistantIntentAsync(command.Message, history, AssistantToolCatalog.Names, ct);

        AssistantTurnResponse turn;
        if (intent.ToolName is null || !ToolDispatcher.IsKnownTool(intent.ToolName))
        {
            // Either Gemini chose plain text, or (defense-in-depth) named something outside the
            // whitelist — either way we never dispatch it, we only ever show a text reply.
            var text = intent.TextReply ?? "I'm not sure how to help with that yet. Try asking me to plan your day, find tasks, or create something new.";
            turn = new AssistantTurnResponse(text, null, null, false, null, null, null);
        }
        else if (!ToolDispatcher.IsMutating(intent.ToolName))
        {
            var args = intent.Arguments ?? new Dictionary<string, object?>();
            var result = await _dispatcher.ExecuteReadOnlyAsync(intent.ToolName, args, ct);
            turn = new AssistantTurnResponse(result.Message, null, null, false, null, null, result.Tasks);
        }
        else
        {
            var args = intent.Arguments ?? new Dictionary<string, object?>();
            var result = await _dispatcher.BuildPreviewAsync(intent.ToolName, args, ct);
            if (!result.Success)
            {
                turn = new AssistantTurnResponse(result.Message, null, null, false, null, null, null);
            }
            else
            {
                var kind = result.Preview switch
                {
                    CreateTaskPreview => "create_task",
                    RescheduleDiffPreview => "reschedule",
                    _ => "generic",
                };
                var wireArgs = args.ToDictionary(
                    kv => kv.Key,
                    kv => JsonSerializer.SerializeToElement(kv.Value));
                turn = new AssistantTurnResponse(result.Message, intent.ToolName, wireArgs, true, kind, result.Preview, null);
            }
        }

        await _aiRepo.AddMessageAsync(new AiMessageEntity
        {
            ConversationId = conversation.Id, Role = "assistant", Content = turn.Reply,
        }, ct);
        await _uow.CommitAsync(ct);

        return Result<AssistantTurnResponse>.Success(turn);
    }

    public async Task<Result<AssistantTurnResponse>> Handle(ConfirmAssistantActionCommand command, CancellationToken ct)
    {
        var toolName = command.Action.ToolName;
        if (!ToolDispatcher.IsKnownTool(toolName) || !ToolDispatcher.IsMutating(toolName))
        {
            return Result<AssistantTurnResponse>.Failure(
                "That isn't a confirmable AI action.",
                new[] { new ValidationError("toolName", "Unknown or non-mutating tool.") });
        }

        var args = command.Action.Arguments.ToDictionary(
            kv => kv.Key,
            object? (kv) => kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.Number => kv.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            });

        var result = await _dispatcher.ApplyAsync(toolName, args, ct);
        var response = new AssistantTurnResponse(result.Message, null, null, false, null, null, null);
        return result.Success
            ? Result<AssistantTurnResponse>.Success(response, result.Message)
            : Result<AssistantTurnResponse>.Failure(result.Message);
    }
}
