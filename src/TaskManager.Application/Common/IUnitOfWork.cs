namespace TaskManager.Application.Common;

public interface IUnitOfWork
{
    Task<int> CommitAsync(CancellationToken ct = default);
}
