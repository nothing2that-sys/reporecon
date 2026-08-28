using System.Collections.Concurrent;

namespace RepoRecon.Core.Concurrency;

public class RepoLockManager : IRepoLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> AcquireLockAsync(string repoAbsPath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(repoAbsPath);
        var semaphore = _locks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    public bool IsLocked(string repoAbsPath)
    {
        var normalizedPath = NormalizePath(repoAbsPath);
        if (_locks.TryGetValue(normalizedPath, out var semaphore))
        {
            return semaphore.CurrentCount == 0;
        }
        return false;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _semaphore, null);
            sem?.Release();
        }
    }
}
