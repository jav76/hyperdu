namespace hyperdu.Core.Scanning;

public static class ScanTier
{
    public const int Low = 0;
    public const int Medium = 1;
    public const int High = 2;
}

/// <summary>
/// Bounded-wait synchronous 3-tier work queue matching PrioritizedQueue tiers
/// (High >= 2 for the emission frontier, Medium == 1 for near-frontier siblings,
/// Low <= 0 for far-ahead subtrees) with lock-guarded backpressure pausing.
/// </summary>
internal sealed class FrontierWorkQueue<T>
{
    private readonly Stack<T> _high = new(64);
    private readonly Stack<T> _medium = new(128);
    private readonly Stack<T> _low = new(256);
    private readonly Func<bool>? _isBackpressureActive;
    private readonly object _lock = new();
    private int _waitingWorkers;
    private bool _completed;

    public FrontierWorkQueue(Func<bool>? isBackpressureActive = null)
    {
        _isBackpressureActive = isBackpressureActive;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _high.Count + _medium.Count + _low.Count;
            }
        }
    }

    public void Enqueue(T item, int priority)
    {
        lock (_lock)
        {
            if (_completed)
                return;

            PushTiered(item, priority);
            if (_waitingWorkers > 0)
                Monitor.Pulse(_lock);
        }
    }

    public void EnqueueBatch(ReadOnlySpan<T> items, int priority)
    {
        if (items.IsEmpty)
            return;

        lock (_lock)
        {
            if (_completed)
                return;

            foreach (T item in items)
                PushTiered(item, priority);

            if (_waitingWorkers > 0)
            {
                if (items.Length == 1)
                    Monitor.Pulse(_lock);
                else
                    Monitor.PulseAll(_lock);
            }
        }
    }

    public void PromoteToHigh(T item)
    {
        lock (_lock)
        {
            if (_completed)
                return;

            _high.Push(item);
            if (_waitingWorkers > 0)
                Monitor.Pulse(_lock);
        }
    }

    public void NotifyBackpressureDrained()
    {
        lock (_lock)
        {
            if (_waitingWorkers > 0)
                Monitor.PulseAll(_lock);
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (_lock)
        {
            while (true)
            {
                if (_high.TryPop(out item!))
                    return true;

                bool paused = _isBackpressureActive != null && _isBackpressureActive();
                if (!paused)
                {
                    if (_medium.TryPop(out item!))
                        return true;

                    if (_low.TryPop(out item!))
                        return true;
                }

                if (_completed)
                {
                    item = default!;
                    return false;
                }

                _waitingWorkers++;
                try
                {
                    Monitor.Wait(_lock);
                }
                finally
                {
                    _waitingWorkers--;
                }
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            Monitor.PulseAll(_lock);
        }
    }

    private void PushTiered(T item, int priority)
    {
        if (priority >= ScanTier.High)
            _high.Push(item);
        else if (priority == ScanTier.Medium)
            _medium.Push(item);
        else
            _low.Push(item);
    }
}
