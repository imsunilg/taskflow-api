namespace TaskManager.Application.AI;

/// <summary>The AI provider returned something we could not parse into the expected shape.</summary>
public class AIServiceException : Exception
{
    public AIServiceException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The AI provider timed out, was unreachable, or returned a transient failure after retries.</summary>
public class AIServiceUnavailableException : Exception
{
    public AIServiceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The calling user has exhausted their daily AI request quota.</summary>
public class AIQuotaExceededException : Exception
{
    public AIQuotaExceededException(string message) : base(message) { }
}
