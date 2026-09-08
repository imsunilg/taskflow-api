using FluentValidation;
using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Tags;

public record TagResponse(Guid Id, Guid UserId, string Name, DateTime CreatedAt);
public record CreateTagRequest(string Name);

public static class TagMapper
{
    public static TagResponse ToResponse(this TagEntity e) => new(e.Id, e.UserId, e.Name, e.CreatedAt);
}

public record GetTagsQuery : IRequest<Result<List<TagResponse>>>;
public record CreateTagCommand(CreateTagRequest Request) : IRequest<Result<TagResponse>>;
public record DeleteTagCommand(Guid Id) : IRequest<Result<bool>>;

public class CreateTagCommandValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagCommandValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(50);
    }
}

public class TagHandlers :
    IRequestHandler<GetTagsQuery, Result<List<TagResponse>>>,
    IRequestHandler<CreateTagCommand, Result<TagResponse>>,
    IRequestHandler<DeleteTagCommand, Result<bool>>
{
    private readonly ITagRepository _repo;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILookupCache _cache;

    public TagHandlers(ITagRepository repo, ICurrentUserService currentUser, IUnitOfWork uow, ILookupCache cache)
    {
        _repo = repo;
        _currentUser = currentUser;
        _uow = uow;
        _cache = cache;
    }

    public async Task<Result<List<TagResponse>>> Handle(GetTagsQuery q, CancellationToken ct)
    {
        var key = ILookupCache.TagsKey(_currentUser.UserId);
        var cached = await _cache.GetAsync<List<TagResponse>>(key, ct);
        if (cached is not null)
        {
            return Result<List<TagResponse>>.Success(cached);
        }

        var items = await _repo.GetAllAsync(_currentUser.UserId, ct);
        var mapped = items.Select(t => t.ToResponse()).ToList();
        await _cache.SetAsync(key, mapped, ct);
        return Result<List<TagResponse>>.Success(mapped);
    }

    private Task Invalidate(CancellationToken ct) =>
        _cache.RemoveAsync(ILookupCache.TagsKey(_currentUser.UserId), ct);

    public async Task<Result<TagResponse>> Handle(CreateTagCommand c, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var existing = await _repo.GetAllAsync(userId, ct);
        if (existing.Any(t => string.Equals(t.Name, c.Request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Result<TagResponse>.Failure("Tag already exists.",
                new[] { new ValidationError("name", "Tag name must be unique.") });
        }

        var entity = new TagEntity { UserId = userId, Name = c.Request.Name.Trim() };
        await _repo.AddAsync(entity, ct);
        await _uow.CommitAsync(ct);
        await Invalidate(ct);
        return Result<TagResponse>.Success(entity.ToResponse(), "Tag created");
    }

    public async Task<Result<bool>> Handle(DeleteTagCommand c, CancellationToken ct)
    {
        var entity = await _repo.GetByIdAsync(_currentUser.UserId, c.Id, ct)
                     ?? throw new NotFoundException("Tag", c.Id);
        var message = await _repo.HasTaggedTasksAsync(c.Id, ct)
            ? "Tag deleted (it was still applied to some tasks)."
            : "Tag deleted";
        _repo.Remove(entity);
        await _uow.CommitAsync(ct);
        await Invalidate(ct);
        return Result<bool>.Success(true, message);
    }
}
