namespace TaskManager.Application.Common;

public class PaginationInfo
{
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public int TotalPages { get; init; }
}

public class ApiResponse<T>
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public T? Data { get; init; }
    public IReadOnlyList<ApiErrorItem> Errors { get; init; } = Array.Empty<ApiErrorItem>();
    public PaginationInfo? Pagination { get; init; }

    public static ApiResponse<T> Ok(T data, string message = "Success", PaginationInfo? pagination = null) =>
        new() { Success = true, Message = message, Data = data, Pagination = pagination };

    public static ApiResponse<T> Fail(string message, IReadOnlyList<ApiErrorItem>? errors = null) =>
        new() { Success = false, Message = message, Errors = errors ?? Array.Empty<ApiErrorItem>() };

    public static ApiResponse<T> FromResult(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value!, result.Message);
        }

        var errors = result.Errors.Select(e => new ApiErrorItem(e.Field, e.Message)).ToList();
        return Fail(result.Message, errors);
    }

    public static ApiResponse<PagedResult<TItem>> FromPaged<TItem>(PagedResult<TItem> paged, string message = "Success")
    {
        return new ApiResponse<PagedResult<TItem>>
        {
            Success = true,
            Message = message,
            Data = paged,
            Pagination = new PaginationInfo
            {
                Page = paged.Page,
                PageSize = paged.PageSize,
                Total = paged.Total,
                TotalPages = paged.TotalPages,
            },
        };
    }
}

public record ApiErrorItem(string Field, string Message);
