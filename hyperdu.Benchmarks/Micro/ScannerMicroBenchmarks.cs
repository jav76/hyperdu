using BenchmarkDotNet.Attributes;
using hyperdu.Benchmarks.Cli;
using hyperdu.Core.Models;
using hyperdu.Core.Scanning;

namespace hyperdu.Benchmarks.Micro;

/// <summary>
/// Microbenchmarks for ParallelScanner across native/managed I/O and worker thread counts.
/// </summary>
[MemoryDiagnoser]
public class ScannerMicroBenchmarks
{
    private FixtureGenerator? _fixtures;
    private string _treeRoot = string.Empty;

    [Params(true, false)]
    public bool UseNativeIo { get; set; }

    [Params(1, 4)]
    public int WorkerCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _fixtures = new FixtureGenerator();
        FixtureSpec spec = _fixtures.CreateBalancedTree(dirCount: 100, fileCount: 2_500, name: "micro-balanced");
        _treeRoot = spec.RootPath;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fixtures?.Dispose();
    }

    [Benchmark]
    public async Task<long> ScanBalancedTree()
    {
        ParallelScanner scanner = new(new ScanOptions
        {
            UseNativeIo = UseNativeIo,
            WorkerCount = WorkerCount
        });

        DirectoryNode root = await scanner.ScanAsync(_treeRoot);
        return root.TotalSize;
    }
}
