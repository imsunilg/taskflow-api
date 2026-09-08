namespace TaskManager.Application.Common;

public record ValidationError(string Field, string Message);

public class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string Message { get; }
    public IReadOnlyList<ValidationError> Errors { get; }

    private Result(bool isSuccess, T? value, string message, IReadOnlyList<ValidationError> errors)
    {
        IsSuccess = isSuccess;
        Value = value;
        Message = message;
        Errors = errors;
    }

    public static Result<T> Success(T value, string message = "Success") =>
        new(true, value, message, Array.Empty<ValidationError>());

    public static Result<T> Failure(string message, IReadOnlyList<ValidationError>? errors = null) =>
        new(false, default, message, errors ?? Array.Empty<ValidationError>());
}
