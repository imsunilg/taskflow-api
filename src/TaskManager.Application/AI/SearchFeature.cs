using FluentValidation;
using MediatR;
using TaskManager.Application.AI.DTOs;
using TaskManager.Application.Common;

namespace TaskManager.Application.AI;

public record NLSearchResultDto(SearchFilterDto Filter, List<TaskSummaryDto> Results);

public record NLSearchCommand(string Query) : IRequest<Result<NLSearchResultDto>>, IAiRequest
{
    public string AiRequestType => "search";
}

public class NLSearchValidator : AbstractValidator<NLSearchCommand>
{
    public NLSearchValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(300);
    }
}

public class NLSearchHandler : IRequestHandler<NLSearchCommand, Result<NLSearchResultDto>>
{
    private readonly IAIService _ai;
    private readonly ToolDispatcher _dispatcher;

    public NLSearchHandler(IAIService ai, ToolDispatcher dispatcher)
    {
        _ai = ai;
        _dispatcher = dispatcher;
    }

    public async Task<Result<NLSearchResultDto>> Handle(NLSearchCommand command, CancellationToken ct)
    {
        var filter = await _ai.ParseSearchQueryAsync(command.Query, ct);
        var results = await _dispatcher.RunSearchFilterAsync(filter, ct);
        return Result<NLSearchResultDto>.Success(new NLSearchResultDto(filter, results));
    }
}
