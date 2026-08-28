using System.Diagnostics.CodeAnalysis;

namespace RepoRecon.Core.Fetch;

public class FetchPriorityQueue
{
    private readonly object _lock = new();
    private readonly List<FetchTask> _items = new();
    private readonly HashSet<long> _queuedRepoIds = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public bool Enqueue(long repoId, string absPath, string hostKey, int priority = 0)
    {
        lock (_lock)
        {
            if (_queuedRepoIds.Contains(repoId))
            {
                // Already in queue, update priority if higher
                var index = _items.FindIndex(x => x.LocalRepoId == repoId);
                if (index >= 0 && priority > _items[index].Priority)
                {
                    _items[index] = _items[index] with { Priority = priority };
                }
                return false;
            }

            _queuedRepoIds.Add(repoId);
            _items.Add(new FetchTask(repoId, absPath, hostKey, priority, DateTime.UtcNow));
            return true;
        }
    }

    public int BoostPriority(IEnumerable<long> repoIds, int priorityBoost = 1000)
    {
        lock (_lock)
        {
            var targetIds = new HashSet<long>(repoIds);
            var boostedCount = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                if (targetIds.Contains(_items[i].LocalRepoId))
                {
                    _items[i] = _items[i] with { Priority = _items[i].Priority + priorityBoost };
                    boostedCount++;
                }
            }
            return boostedCount;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out FetchTask? task)
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                task = null;
                return false;
            }

            // Find item with highest priority; if tied, earliest EnqueuedAt
            var bestIndex = 0;
            for (var i = 1; i < _items.Count; i++)
            {
                if (_items[i].Priority > _items[bestIndex].Priority)
                {
                    bestIndex = i;
                }
                else if (_items[i].Priority == _items[bestIndex].Priority &&
                         _items[i].EnqueuedAt < _items[bestIndex].EnqueuedAt)
                {
                    bestIndex = i;
                }
            }

            task = _items[bestIndex];
            _items.RemoveAt(bestIndex);
            _queuedRepoIds.Remove(task.LocalRepoId);
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _queuedRepoIds.Clear();
        }
    }

    public IReadOnlyList<long> GetQueuedRepoIds()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(x => x.Priority).ThenBy(x => x.EnqueuedAt).Select(x => x.LocalRepoId).ToList();
        }
    }
}
