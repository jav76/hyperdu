using System.Text;
using hyperdu.Core.Du;

namespace hyperdu.Core.Models;

public class DirectoryNode
{
    private long _endTicks;

    internal int _fileCount;

    internal int _selfFileCount;

    private int _scanClaimed;

    private long _startTicks;

    internal int _subdirectoryCount;

    internal long _totalSize;

    public DirectoryNode(string path, DirectoryNode? parent = null) : this(path, parent, null, null)
    {
    }

    internal DirectoryNode(string path, DirectoryNode? parent, byte[]? nativeName, byte[]? nativeRootPath)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        Parent = parent;
        NativeName = nativeName;
        _nativeRootPath = nativeRootPath;
        HasNativeAncestry = nativeName != null || nativeRootPath != null || parent?.HasNativeAncestry == true;
    }

    public string Path { get; }
    public string Name { get; }

    /// <summary>
    /// Raw name bytes, set only when the name is not valid UTF-8 and so cannot be recovered from <see cref="Name"/>.
    /// </summary>
    internal byte[]? NativeName { get; }

    /// <summary>
    /// True when this node or an ancestor has a name that <see cref="Path"/> cannot represent exactly.
    /// </summary>
    internal bool HasNativeAncestry { get; }

    private readonly byte[]? _nativeRootPath;
    internal FileIdentity Identity { get; set; }
    public DirectoryNode? Parent { get; set; }
    public long SelfSize { get; set; }

    public long TotalSize
    {
        get => Volatile.Read(ref _totalSize);
        set => Volatile.Write(ref _totalSize, value);
    }

    public int SubdirectoryCount
    {
        get => Volatile.Read(ref _subdirectoryCount);
        set => Volatile.Write(ref _subdirectoryCount, value);
    }

    public int FileCount
    {
        get => Volatile.Read(ref _fileCount);
        set => Volatile.Write(ref _fileCount, value);
    }

    /// <summary>
    /// Number of immediate files in this directory (not recursive).
    /// Files are not stored in memory; this counter enables display without heap allocations.
    /// </summary>
    public int SelfFileCount
    {
        get => Volatile.Read(ref _selfFileCount);
        set => Volatile.Write(ref _selfFileCount, value);
    }

    public List<DirectoryNode> Subdirectories { get; } = new();
    public string? ErrorMessage { get; set; }

    public bool IsScanned { get; set; }

    /// <summary>
    /// Atomically claims this node for scanning. Returns true if this caller won the claim.
    /// Replaces the scannedPaths ConcurrentDictionary for deduplication.
    /// </summary>
    public bool TryClaimForScan() => Interlocked.CompareExchange(ref _scanClaimed, 1, 0) == 0;

    public long StartTicks
    {
        get => Volatile.Read(ref _startTicks);
        set => Volatile.Write(ref _startTicks, value);
    }

    public long EndTicks
    {
        get => Volatile.Read(ref _endTicks);
        set => Volatile.Write(ref _endTicks, value);
    }

    public TimeSpan TotalScanTime
    {
        get
        {
            long maxEnd = GetMaxEndTicksRecursive();
            long start = StartTicks;
            return TimeSpan.FromTicks(Math.Max(0, maxEnd - start));
        }
    }

    private long GetMaxEndTicksRecursive()
    {
        long maxEnd = EndTicks;
        DirectoryNode[] subs;
        lock (Subdirectories)
        {
            subs = Subdirectories.ToArray();
        }

        foreach (DirectoryNode sub in subs)
        {
            long subMax = sub.GetMaxEndTicksRecursive();
            if (subMax > maxEnd) maxEnd = subMax;
        }

        return maxEnd;
    }

    /// <summary>
    /// Recursively trims over-allocated List backing arrays after scanning completes.
    /// Reclaims ~50% of wasted capacity from List doubling growth strategy.
    /// </summary>
    public void Compact()
    {
        lock (Subdirectories)
        {
            Subdirectories.TrimExcess();
        }

        foreach (DirectoryNode sub in Subdirectories)
            sub.Compact();
    }

    /// <summary>
    /// Exact path bytes for native calls. Rebuilt from the parent chain only when some name on it is not valid UTF-8.
    /// </summary>
    internal byte[] GetNativePath(bool nullTerminated = true)
    {
        if (!HasNativeAncestry)
        {
            int length = Encoding.UTF8.GetByteCount(Path);
            byte[] bytes = new byte[nullTerminated ? length + 1 : length];
            Encoding.UTF8.GetBytes(Path, bytes);
            return bytes;
        }

        List<byte> buffer = new List<byte>(Path.Length + 1);
        AppendNativePath(buffer);
        if (nullTerminated) buffer.Add(0);
        return buffer.ToArray();
    }

    private void AppendNativePath(List<byte> buffer)
    {
        if (Parent == null || !HasNativeAncestry)
        {
            buffer.AddRange(_nativeRootPath ?? Encoding.UTF8.GetBytes(Path));
            return;
        }

        Parent.AppendNativePath(buffer);
        if (buffer.Count == 0 || buffer[^1] != (byte)'/') buffer.Add((byte)'/');
        buffer.AddRange(NativeName ?? Encoding.UTF8.GetBytes(Name));
    }

    /// <summary>
    /// Thread-safely updates the size, subdirectory count, and file count of this node.
    /// </summary>
    public void AddDelta(long sizeDelta, int subdirsDelta, int filesDelta)
    {
        if (sizeDelta != 0) Interlocked.Add(ref _totalSize, sizeDelta);
        if (subdirsDelta != 0) Interlocked.Add(ref _subdirectoryCount, subdirsDelta);
        if (filesDelta != 0) Interlocked.Add(ref _fileCount, filesDelta);
    }
}