namespace RepoRecon.Core.Concurrency;

public interface IRepoLockManager
{
    Task<IDisposable> AcquireLockAsync(string repoAbsPath, CancellationToken cancellationToken = default);
    bool IsLocked(string repoAbsPath);
}
