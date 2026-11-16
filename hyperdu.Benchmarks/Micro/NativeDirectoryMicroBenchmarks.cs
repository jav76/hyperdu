using System.Text;
using BenchmarkDotNet.Attributes;
using hyperdu.Benchmarks.Cli;
using hyperdu.Core.Native;
using hyperdu.Core.Scanning;

namespace hyperdu.Benchmarks.Micro;

/// <summary>
/// Microbenchmarks comparing NativeDirectory (libc readdir + statx/fstatat) vs LightweightFileSystemEnumerator.
/// </summary>
[MemoryDiagnoser]
public class NativeDirectoryMicroBenchmarks
{
    private FixtureGenerator? _fixtures;
    private string _hotDir = string.Empty;
    private byte[] _hotDirNullTerminated = [];
    private EnumerationOptions _enumOptions = new();

    [GlobalSetup]
    public void Setup()
    {
        _fixtures = new FixtureGenerator();
        FixtureSpec spec = _fixtures.CreateWideDir(fileCount: 5_000, siblingDirs: 0, name: "micro-wide");
        _hotDir = Path.Combine(spec.RootPath, "hot");
        _hotDirNullTerminated = Encoding.UTF8.GetBytes(_hotDir + "\0");
        _enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fixtures?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public long LightweightEnumerator_ReadAll()
    {
        long total = 0;
        using LightweightFileSystemEnumerator enumerator = new(_hotDir, _enumOptions);
        while (enumerator.MoveNext())
        {
            total += enumerator.Current.Length;
        }
        return total;
    }

    [Benchmark]
    public unsafe long NativeDirectory_ReadAndStatAll()
    {
        if (!NativeIo.IsSupported)
            return -1;

        long total = 0;
        Span<byte> statBuffer = stackalloc byte[NativeIo.StatBufferSize];
        using NativeDirectory? directory = NativeDirectory.Open(_hotDirNullTerminated, out _);
        if (directory == null)
            return 0;

        while (directory.MoveNext())
        {
            if (directory.TryStatCurrent(false, statBuffer, out NativeStat stat))
                total += stat.Size;
        }

        return total;
    }
}
