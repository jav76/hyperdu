using hyperdu.Core.Models;

namespace hyperdu.Core.Scanning;

public class PrioritizedQueue
{
    private readonly Queue<DirectoryNode> _highQueue = new(); // Priority 2 (selected path)
    private readonly HashSet<DirectoryNode> _inQueue = new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();
    private readonly Queue<DirectoryNode> _lowQueue = new(); // Priority 0 (other paths)
    private readonly Queue<DirectoryNode> _mediumQueue = new(); // Priority 1 (current path)
    private readonly SemaphoreSlim _semaphore = new(0);
    private bool _isCompleted;
    private Func<DirectoryNode, int>? _priorityEvaluator;

    public void SetPriorityEvaluator(Func<DirectoryNode, int> priorityEvaluator)
    {
        lock (_lock)
        {
            _priorityEvaluator = priorityEvaluator;
            ReprioritizeQueue();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _highQueue.Clear();
            _mediumQueue.Clear();
            _lowQueue.Clear();
            _inQueue.Clear();
            _isCompleted = false;
            while (_semaphore.CurrentCount > 0) _semaphore.Wait(0);
        }
    }

    public void Enqueue(DirectoryNode node)
    {
        lock (_lock)
        {
            if (_isCompleted) return;
            if (!_inQueue.Add(node)) return;

            int priority = _priorityEvaluator?.Invoke(node) ?? 0;
            if (priority >= 2)
                _highQueue.Enqueue(node);
            else if (priority == 1)
                _mediumQueue.Enqueue(node);
            else
                _lowQueue.Enqueue(node);
        }

        _semaphore.Release();
    }

    public async Task<(bool Success, DirectoryNode? Node)> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _semaphore.WaitAsync(cancellationToken);

            lock (_lock)
            {
                if (_highQueue.Count > 0)
                {
                    DirectoryNode node = _highQueue.Dequeue();
                    _inQueue.Remove(node);
                    return (true, node);
                }

                if (_mediumQueue.Count > 0)
                {
                    DirectoryNode node = _mediumQueue.Dequeue();
                    _inQueue.Remove(node);
                    return (true, node);
                }

                if (_lowQueue.Count > 0)
                {
                    DirectoryNode node = _lowQueue.Dequeue();
                    _inQueue.Remove(node);
                    return (true, node);
                }

                if (_isCompleted) return (false, null);
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _isCompleted = true;
        }

        _semaphore.Release(100);
    }

    private void ReprioritizeQueue()
    {
        if (_priorityEvaluator == null) return;

        List<DirectoryNode> allItems = new List<DirectoryNode>(_highQueue.Count + _mediumQueue.Count + _lowQueue.Count);

        while (_highQueue.Count > 0) allItems.Add(_highQueue.Dequeue());
        while (_mediumQueue.Count > 0) allItems.Add(_mediumQueue.Dequeue());
        while (_lowQueue.Count > 0) allItems.Add(_lowQueue.Dequeue());

        foreach (DirectoryNode node in allItems)
        {
            int priority = _priorityEvaluator(node);
            if (priority >= 2)
                _highQueue.Enqueue(node);
            else if (priority == 1)
                _mediumQueue.Enqueue(node);
            else
                _lowQueue.Enqueue(node);
        }
    }
}