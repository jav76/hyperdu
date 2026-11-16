using BenchmarkDotNet.Attributes;
using hyperdu.Core.Models;
using hyperdu.Core.Scanning;

namespace hyperdu.Benchmarks.Micro;

/// <summary>
/// Microbenchmarks for PrioritizedQueue throughput and dynamic subtree reprioritization latency.
/// </summary>
[MemoryDiagnoser]
public class PrioritizedQueueMicroBenchmarks
{
    private const int SubtreeCount = 20;
    private const int NodesPerSubtree = 1_000;
    private DirectoryNode[] _nodes = [];
    private string[] _subtreeRoots = [];

    [GlobalSetup]
    public void Setup()
    {
        _subtreeRoots = new string[SubtreeCount];
        _nodes = new DirectoryNode[SubtreeCount * NodesPerSubtree];

        int index = 0;
        for (int s = 0; s < SubtreeCount; s++)
        {
            string subRoot = $"/bench/sub_{s:D2}";
            _subtreeRoots[s] = subRoot;
            for (int n = 0; n < NodesPerSubtree; n++)
            {
                _nodes[index++] = new DirectoryNode($"{subRoot}/dir_{n:D4}");
            }
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<int> EnqueueAndDrain_UniformPriority()
    {
        PrioritizedQueue queue = new();
        foreach (DirectoryNode node in _nodes)
        {
            queue.Enqueue(node);
        }

        queue.Complete();

        int count = 0;
        while (true)
        {
            (bool ok, _) = await queue.DequeueAsync(CancellationToken.None);
            if (!ok)
                break;
            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> Reprioritize_CursorJumpAcrossSubtrees()
    {
        PrioritizedQueue queue = new();
        foreach (DirectoryNode node in _nodes)
        {
            queue.Enqueue(node);
        }

        int[] focusTargets = [3, 7, 11, 15, 19];
        foreach (int targetIndex in focusTargets)
        {
            string highPrefix = _subtreeRoots[targetIndex];
            string mediumPrefix = _subtreeRoots[(targetIndex + 1) % SubtreeCount];
            queue.SetPriorityEvaluator(node =>
            {
                if (node.Path.StartsWith(highPrefix, StringComparison.Ordinal))
                    return 2;
                if (node.Path.StartsWith(mediumPrefix, StringComparison.Ordinal))
                    return 1;
                return 0;
            });
        }

        queue.Complete();

        int drainedHigh = 0;
        for (int i = 0; i < NodesPerSubtree; i++)
        {
            (bool ok, DirectoryNode? node) = await queue.DequeueAsync(CancellationToken.None);
            if (!ok || node == null)
                break;
            drainedHigh++;
        }

        return drainedHigh;
    }
}
