namespace hyperdu.Benchmarks.Cli;

/// <summary>
/// Generates deterministic synthetic directory trees for reproducible CLI and microbenchmarks.
/// </summary>
internal sealed class FixtureGenerator : IDisposable
{
    private const int FixedSeed = 42;
    private readonly string _baseDirectory;
    private readonly bool _keepFixtures;
    private readonly List<string> _createdRoots = new();

    public FixtureGenerator(string? baseDirectory = null, bool keepFixtures = false)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(Path.GetTempPath(), $"hyperdu-bench-{Guid.NewGuid():N}");
        _keepFixtures = keepFixtures;
        Directory.CreateDirectory(_baseDirectory);
    }

    public string BaseDirectory => _baseDirectory;

    /// <summary>Generates all synthetic fixtures required by the chosen benchmark suite.</summary>
    public IReadOnlyList<FixtureSpec> GenerateSuite(BenchmarkSuite suite)
    {
        List<FixtureSpec> specs = new()
        {
            CreateTinyStartup(),
            CreateBalancedTree(
                dirCount: suite == BenchmarkSuite.Quick ? 100 : 500,
                fileCount: suite == BenchmarkSuite.Quick ? 2_500 : 50_000)
        };

        if (suite is BenchmarkSuite.Standard or BenchmarkSuite.Full)
        {
            specs.Add(CreateWideDir(fileCount: 20_000, siblingDirs: 10));
            specs.Add(CreateDeepChain(depth: 250));
            specs.Add(CreateHardlinksHeavy(uniqueFiles: 5_000, hardlinkCount: 5_000, symlinkCount: 100));
        }

        if (suite == BenchmarkSuite.Full)
        {
            specs.Add(CreateMillionFiles(dirCount: 1_000, filesPerDir: 1_000));
        }

        return specs;
    }

    /// <summary>Tiny 1-directory, 5-file tree to isolate binary startup overhead.</summary>
    public FixtureSpec CreateTinyStartup(string name = "tiny-startup")
    {
        string root = AllocateFixtureRoot(name);
        byte[] payload = CreatePatternBuffer(1024);
        for (int i = 0; i < 5; i++)
        {
            WriteFileSpan(Path.Combine(root, $"file_{i}.bin"), payload.AsSpan(0, 128 * (i + 1)));
        }

        return new FixtureSpec(name, root, "1 dir, 5 small files (startup latency)", 5, 1, IsSynthetic: true);
    }

    /// <summary>Balanced multi-level directory tree with mixed file sizes and periodic .tmp files for exclude tests.</summary>
    public FixtureSpec CreateBalancedTree(int dirCount, int fileCount, string name = "balanced-tree")
    {
        string root = AllocateFixtureRoot(name);
        List<string> dirs = BuildBalancedDirectoryGrid(root, dirCount);
        byte[] sharedPayload = CreatePatternBuffer(16 * 1024);

        Parallel.For(0, fileCount, i =>
        {
            string dir = dirs[i % dirs.Count];
            int size = DeterministicSize(i, 512, sharedPayload.Length);
            string ext = (i % 50 == 0) ? "tmp" : "dat";
            string filePath = Path.Combine(dir, $"f_{i:D6}.{ext}");
            WriteFileSpan(filePath, sharedPayload.AsSpan(0, size));
        });

        return new FixtureSpec(
            name,
            root,
            $"{dirs.Count:N0} dirs across depth <= 6, {fileCount:N0} files",
            fileCount,
            dirs.Count,
            IsSynthetic: true);
    }

    /// <summary>Single wide directory with many entries to test readdir throughput and gnulib fts sorting thresholds.</summary>
    public FixtureSpec CreateWideDir(int fileCount, int siblingDirs, string name = "wide-dir-20k")
    {
        string root = AllocateFixtureRoot(name);
        string hotDir = Path.Combine(root, "hot");
        Directory.CreateDirectory(hotDir);

        byte[] payload = CreatePatternBuffer(1024);
        Parallel.For(0, fileCount, i =>
        {
            int size = DeterministicSize(i, 64, payload.Length);
            WriteFileSpan(Path.Combine(hotDir, $"entry_{i:D5}.bin"), payload.AsSpan(0, size));
        });

        int totalDirs = 2; // root + hot
        int totalFiles = fileCount;
        for (int d = 0; d < siblingDirs; d++)
        {
            string sibling = Path.Combine(root, $"sibling_{d:D2}");
            Directory.CreateDirectory(sibling);
            totalDirs++;
            for (int f = 0; f < 10; f++)
            {
                WriteFileSpan(Path.Combine(sibling, $"s_{f}.dat"), payload.AsSpan(0, 256));
                totalFiles++;
            }
        }

        return new FixtureSpec(
            name,
            root,
            $"1 hot dir with {fileCount:N0} files + {siblingDirs} sibling dirs",
            totalFiles,
            totalDirs,
            IsSynthetic: true);
    }

    /// <summary>Deeply nested directory chain exceeding PATH_MAX on Linux/macOS to test fd-relative traversal.</summary>
    public FixtureSpec CreateDeepChain(int depth, string name = "deep-chain")
    {
        string root = AllocateFixtureRoot(name);
        (int createdDirs, int createdFiles) = UnixNativeFs.CreateDeepChain(root, depth, fileInterval: 10, fileSize: 256);

        return new FixtureSpec(
            name,
            root,
            $"{createdDirs} nested levels (fd-relative deep chain), {createdFiles} leaf files",
            createdFiles,
            createdDirs,
            IsSynthetic: true);
    }

    /// <summary>Dense hard links across sibling directories plus relative symlinks to test inode deduplication.</summary>
    public FixtureSpec CreateHardlinksHeavy(
        int uniqueFiles,
        int hardlinkCount,
        int symlinkCount,
        string name = "hardlinks-heavy")
    {
        string root = AllocateFixtureRoot(name);
        const int bucketCount = 50;
        string[] primaryDirs = new string[bucketCount];
        string[] linkDirs = new string[bucketCount];

        for (int b = 0; b < bucketCount; b++)
        {
            primaryDirs[b] = Path.Combine(root, "primary", $"b_{b:D2}");
            linkDirs[b] = Path.Combine(root, "links", $"b_{b:D2}");
            Directory.CreateDirectory(primaryDirs[b]);
            Directory.CreateDirectory(linkDirs[b]);
        }

        string symDir = Path.Combine(root, "symlinks");
        Directory.CreateDirectory(symDir);

        byte[] payload = CreatePatternBuffer(4096);
        string[] sourcePaths = new string[uniqueFiles];
        Parallel.For(0, uniqueFiles, i =>
        {
            string path = Path.Combine(primaryDirs[i % bucketCount], $"src_{i:D5}.bin");
            int size = DeterministicSize(i, 512, payload.Length);
            WriteFileSpan(path, payload.AsSpan(0, size));
            sourcePaths[i] = path;
        });

        Parallel.For(0, hardlinkCount, i =>
        {
            string source = sourcePaths[i % uniqueFiles];
            string target = Path.Combine(linkDirs[(i + 1) % bucketCount], $"hl_{i:D5}.bin");
            if (!UnixNativeFs.TryCreateHardLink(source, target))
            {
                File.Copy(source, target, overwrite: true);
            }
        });

        for (int s = 0; s < symlinkCount; s++)
        {
            string targetRel = Path.Combine("..", "primary", $"b_{s % bucketCount:D2}", $"src_{s:D5}.bin");
            File.CreateSymbolicLink(Path.Combine(symDir, $"sl_{s:D3}"), targetRel);
        }

        int totalDirs = 1 + 1 + bucketCount + 1 + bucketCount + 1; // root + primary + buckets + links + buckets + symlinks
        int totalFiles = uniqueFiles + hardlinkCount + symlinkCount;

        return new FixtureSpec(
            name,
            root,
            $"{uniqueFiles:N0} base files + {hardlinkCount:N0} hard links + {symlinkCount:N0} symlinks",
            totalFiles,
            totalDirs,
            IsSynthetic: true);
    }

    /// <summary>1M-file corpus across 1,000 directories to stress du -a streaming backpressure and Peak RSS.</summary>
    public FixtureSpec CreateMillionFiles(int dirCount = 1_000, int filesPerDir = 1_000, string name = "million-files")
    {
        string root = AllocateFixtureRoot(name);
        string[] dirs = new string[dirCount];
        for (int d = 0; d < dirCount; d++)
        {
            string dir = Path.Combine(root, $"g_{d / 50:D2}", $"d_{d:D4}");
            Directory.CreateDirectory(dir);
            dirs[d] = dir;
        }

        byte[] smallPayload = CreatePatternBuffer(64);
        Parallel.For(0, dirCount, d =>
        {
            string dir = dirs[d];
            for (int f = 0; f < filesPerDir; f++)
            {
                // Keep 90% zero-byte files so 1M files do not exhaust CI disk blocks while still exercising 1M inodes.
                ReadOnlySpan<byte> data = (f % 10 == 0) ? smallPayload : ReadOnlySpan<byte>.Empty;
                WriteFileSpan(Path.Combine(dir, $"m_{f:D4}"), data);
            }
        });

        int groupDirs = (dirCount + 49) / 50;
        return new FixtureSpec(
            name,
            root,
            $"{dirCount:N0} dirs x {filesPerDir:N0} files ({dirCount * filesPerDir:N0} total files)",
            dirCount * filesPerDir,
            1 + groupDirs + dirCount,
            IsSynthetic: true);
    }

    public void Dispose()
    {
        if (_keepFixtures)
            return;

        foreach (string root in _createdRoots)
        {
            try
            {
                UnixNativeFs.DeleteDirectoryRecursive(root);
            }
            catch
            {
                // Best-effort cleanup of temp fixtures.
            }
        }

        try
        {
            if (Directory.Exists(_baseDirectory))
                Directory.Delete(_baseDirectory, true);
        }
        catch
        {
            // Best-effort cleanup of base temp directory.
        }
    }

    private string AllocateFixtureRoot(string name)
    {
        string root = Path.Combine(_baseDirectory, name);
        Directory.CreateDirectory(root);
        _createdRoots.Add(root);
        return root;
    }

    private static List<string> BuildBalancedDirectoryGrid(string root, int targetDirCount)
    {
        List<string> dirs = new(targetDirCount) { root };
        int queueIndex = 0;
        int counter = 0;

        while (dirs.Count < targetDirCount && queueIndex < dirs.Count)
        {
            string parent = dirs[queueIndex++];
            int depth = GetRelativeDepth(root, parent);
            if (depth >= 5)
                continue;

            int fanout = Math.Min(4, targetDirCount - dirs.Count);
            for (int c = 0; c < fanout; c++)
            {
                string child = Path.Combine(parent, $"dir_{counter++:D4}");
                Directory.CreateDirectory(child);
                dirs.Add(child);
            }
        }

        while (dirs.Count < targetDirCount)
        {
            string flat = Path.Combine(root, $"dir_{counter++:D4}");
            Directory.CreateDirectory(flat);
            dirs.Add(flat);
        }

        return dirs;
    }

    private static int GetRelativeDepth(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.Ordinal))
            return 0;

        string rel = Path.GetRelativePath(root, path);
        int depth = 1;
        foreach (char ch in rel)
        {
            if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
                depth++;
        }

        return depth;
    }

    private static int DeterministicSize(int index, int minSize, int maxSize)
    {
        uint hash = (uint)(index * 2654435761u) ^ FixedSeed;
        int range = Math.Max(1, maxSize - minSize + 1);
        return minSize + (int)(hash % (uint)range);
    }

    private static byte[] CreatePatternBuffer(int length)
    {
        byte[] buffer = new byte[length];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)('A' + (i % 26));
        }
        return buffer;
    }

    private static void WriteFileSpan(string path, ReadOnlySpan<byte> data)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096);
        if (!data.IsEmpty)
            stream.Write(data);
    }
}
