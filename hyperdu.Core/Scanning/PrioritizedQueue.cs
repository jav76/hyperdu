using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace hyperdu.Core.Scanning;

public class PrioritizedQueue
{
    private readonly Queue<string> _highQueue = new();    // Priority 2 (selected path)
    private readonly Queue<string> _mediumQueue = new();  // Priority 1 (current path)
    private readonly Queue<string> _lowQueue = new();     // Priority 0 (other paths)
    private readonly HashSet<string> _inQueue = new();
    private readonly SemaphoreSlim _semaphore = new(0);
    private readonly object _lock = new();
    private bool _isCompleted;
    private Func<string, int>? _priorityEvaluator;

    public void SetPriorityEvaluator(Func<string, int> priorityEvaluator)
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
            while (_semaphore.CurrentCount > 0)
            {
                _semaphore.Wait(0);
            }
        }
    }

    public void Enqueue(string path)
    {
        lock (_lock)
        {
            if (_isCompleted) return;
            if (!_inQueue.Add(path)) return; // Already in queue

            int priority = _priorityEvaluator?.Invoke(path) ?? 0;
            if (priority >= 2)
            {
                _highQueue.Enqueue(path);
            }
            else if (priority == 1)
            {
                _mediumQueue.Enqueue(path);
            }
            else
            {
                _lowQueue.Enqueue(path);
            }
        }
        _semaphore.Release();
    }

    public async Task<(bool Success, string Path)> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _semaphore.WaitAsync(cancellationToken);

            lock (_lock)
            {
                if (_highQueue.Count > 0)
                {
                    string path = _highQueue.Dequeue();
                    _inQueue.Remove(path);
                    return (true, path);
                }
                if (_mediumQueue.Count > 0)
                {
                    string path = _mediumQueue.Dequeue();
                    _inQueue.Remove(path);
                    return (true, path);
                }
                if (_lowQueue.Count > 0)
                {
                    string path = _lowQueue.Dequeue();
                    _inQueue.Remove(path);
                    return (true, path);
                }

                if (_isCompleted)
                {
                    return (false, string.Empty);
                }
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _isCompleted = true;
        }
        _semaphore.Release(100); // Wake up all waiting worker tasks
    }

    private void ReprioritizeQueue()
    {
        if (_priorityEvaluator == null) return;

        // Collect all items currently in the queues
        var allItems = new List<string>(_highQueue.Count + _mediumQueue.Count + _lowQueue.Count);
        
        while (_highQueue.Count > 0) allItems.Add(_highQueue.Dequeue());
        while (_mediumQueue.Count > 0) allItems.Add(_mediumQueue.Dequeue());
        while (_lowQueue.Count > 0) allItems.Add(_lowQueue.Dequeue());

        // Re-enqueue them into the correct buckets
        foreach (var path in allItems)
        {
            int priority = _priorityEvaluator(path);
            if (priority >= 2)
            {
                _highQueue.Enqueue(path);
            }
            else if (priority == 1)
            {
                _mediumQueue.Enqueue(path);
            }
            else
            {
                _lowQueue.Enqueue(path);
            }
        }
    }
}
