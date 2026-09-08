using FluentValidation;
using MediatR;
using TaskManager.Application.Common;
using TaskManager.Application.Interfaces;
using TaskManager.Domain.Entities;

namespace TaskManager.Application.Categories;

public record CategoryResponse(Guid Id, Guid UserId, string Name, string Color, string? Icon,
    DateTime CreatedAt, DateTime UpdatedAt);

public record CreateCategoryRequest(string Name, string Color, string? Icon);
public record UpdateCategoryRequest(string? Name, string? Color, string? Icon);

public static class CategoryMapper
{
    public static CategoryResponse ToResponse(this CategoryEntity e) =>
        new(e.Id, e.UserId, e.Name, e.Color, e.Icon, e.CreatedAt, e.UpdatedAt);
}

public record GetCategoriesQuery : IRequest<Result<List<CategoryResponse>>>;
public record CreateCategoryCommand(CreateCategoryRequest Request) : IRequest<Result<CategoryResponse>>;
public record UpdateCategoryCommand(Guid Id, UpdateCategoryRequest Request) : IRequest<Result<CategoryResponse>>;
public record DeleteCategoryCommand(Guid Id) : IRequest<Result<bool>>;

public class CreateCategoryCommandValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryCommandValidator()
    {
        RuleFor(x => x.Request.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Request.Color).NotEmpty().Matches("^#([0-9a-fA-F]{6})$")
            .WithMessage("Color must be a hex value like #3b82f6.");
    }
}

public class CategoryHandlers :
    IRequestHandler<GetCategoriesQuery, Result<List<CategoryResponse>>>,
    IRequestHandler<CreateCategoryCommand, Result<CategoryResponse>>,
    IRequestHandler<UpdateCategoryCommand, Result<CategoryResponse>>,
    IRequestHandler<DeleteCategoryCommand, Result<bool>>
{
    private readonly ICategoryRepository _repo;
    private readonly ICurrentUserService _currentUser;
    private readonly IUnitOfWork _uow;
    private readonly ILookupCache _cache;

    public CategoryHandlers(ICategoryRepository repo, ICurrentUserService currentUser, IUnitOfWork uow, ILookupCache cache)
    {
        _repo = repo;
        _currentUser = currentUser;
        _uow = uow;
        _cache = cache;
    }

    public async Task<Result<List<CategoryResponse>>> Handle(GetCategoriesQuery q, CancellationToken ct)
    {
        var key = ILookupCache.CategoriesKey(_currentUser.UserId);
        var cached = await _cache.GetAsync<List<CategoryResponse>>(key, ct);
        if (cached is not null)
        {
            return Result<List<CategoryResponse>>.Success(cached);
        }

        var items = await _repo.GetAllAsync(_currentUser.UserId, ct);
        var mapped = items.Select(c => c.ToResponse()).ToList();
        await _cache.SetAsync(key, mapped, ct);
        return Result<List<CategoryResponse>>.Success(mapped);
    }

    private Task Invalidate(CancellationToken ct) =>
        _cache.RemoveAsync(ILookupCache.CategoriesKey(_currentUser.UserId), ct);

    public async Task<Result<CategoryResponse>> Handle(CreateCategoryCommand c, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (await _repo.NameExistsAsync(userId, c.Request.Name, null, ct))
        {
            return Result<CategoryResponse>.Failure("A category with that name already exists.",
                new[] { new ValidationError("name", "Name must be unique.") });
        }

        var entity = new CategoryEntity
        {
            UserId = userId,
            Name = c.Request.Name.Trim(),
            Color = c.Request.Color,
            Icon = c.Request.Icon,
        };
        await _repo.AddAsync(entity, ct);
        await _uow.CommitAsync(ct);
        await Invalidate(ct);
        return Result<CategoryResponse>.Success(entity.ToResponse(), "Category created");
    }

    public async Task<Result<CategoryResponse>> Handle(UpdateCategoryCommand c, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var entity = await _repo.GetByIdAsync(userId, c.Id, ct)
                     ?? throw new NotFoundException("Category", c.Id);

        if (c.Request.Name is not null)
        {
            if (await _repo.NameExistsAsync(userId, c.Request.Name, c.Id, ct))
            {
                return Result<CategoryResponse>.Failure("A category with that name already exists.",
                    new[] { new ValidationError("name", "Name must be unique.") });
            }
            entity.Name = c.Request.Name.Trim();
        }
        if (c.Request.Color is not null) entity.Color = c.Request.Color;
        if (c.Request.Icon is not null) entity.Icon = c.Request.Icon;

        await _uow.CommitAsync(ct);
        await Invalidate(ct);
        return Result<CategoryResponse>.Success(entity.ToResponse(), "Category updated");
    }

    public async Task<Result<bool>> Handle(DeleteCategoryCommand c, CancellationToken ct)
    {
        var entity = await _repo.GetByIdAsync(_currentUser.UserId, c.Id, ct)
                     ?? throw new NotFoundException("Category", c.Id);
        _repo.Remove(entity);
        await _uow.CommitAsync(ct);
        await Invalidate(ct);
        return Result<bool>.Success(true, "Category deleted");
    }
}
