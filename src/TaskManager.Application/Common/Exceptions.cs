namespace TaskManager.Application.Common;

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }

    public NotFoundException(string entity, object key)
        : base($"{entity} with key '{key}' was not found.") { }
}

public class ForbiddenException : Exception
{
    public ForbiddenException(string message = "You do not have access to this resource.")
        : base(message) { }
}

public class ValidationException : Exception
{
    public IReadOnlyList<ValidationError> Errors { get; }

    public ValidationException(IReadOnlyList<ValidationError> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }
}
