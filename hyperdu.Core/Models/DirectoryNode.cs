using System.Buffers;
using System.Text;
using hyperdu.Core.Du;
using hyperdu.Core.Native;

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

    private string? _path;

    private NativeDirectory? _preopenedDir;

    public DirectoryNode(string path, DirectoryNode? parent = null) : this(path, parent, null, null)
    {
    }

    internal DirectoryNode(string path, DirectoryNode? parent, byte[]? nativeName, byte[]? nativeRootPath)
    {
        _path = path;
        Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        Parent = parent;
        Depth = parent != null ? parent.Depth + 1 : 0;
        NativeName = nativeName;
        _nativeRootPath = nativeRootPath;
        HasNativeAncestry = nativeName != null || nativeRootPath != null || parent?.HasNativeAncestry == true;
    }

    internal DirectoryNode(DirectoryNode parent, string name, byte[]? nativeName)
    {
        _path = null;
        Name = name;
        Parent = parent;
        Depth = parent.Depth + 1;
        NativeName = nativeName;
        _nativeRootPath = null;
        HasNativeAncestry = nativeName != null || parent.HasNativeAncestry;
    }

    public string Path => Volatile.Read(ref _path) ?? MaterializePath();
    public string Name { get; }
    internal int Depth { get; }
    internal int PreopenError { get; set; }

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

    internal void SetPreopenedDirectory(NativeDirectory dir)
    {
        NativeDirectory? old = Interlocked.Exchange(ref _preopenedDir, dir);
        old?.Dispose();
    }

    internal NativeDirectory? TakePreopenedDirectory() =>
        Interlocked.Exchange(ref _preopenedDir, null);

    internal void DisposePreopenedDirectory() =>
        Interlocked.Exchange(ref _preopenedDir, null)?.Dispose();

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
        Stack<DirectoryNode>? stack = null;

        DirectoryNode[] rootSubs;
        lock (Subdirectories)
        {
            if (Subdirectories.Count == 0)
                return maxEnd;
            rootSubs = Subdirectories.ToArray();
        }

        stack = new Stack<DirectoryNode>(rootSubs.Length);
        for (int i = 0; i < rootSubs.Length; i++)
            stack.Push(rootSubs[i]);

        while (stack.Count > 0)
        {
            DirectoryNode current = stack.Pop();
            long end = current.EndTicks;
            if (end > maxEnd)
                maxEnd = end;

            lock (current.Subdirectories)
            {
                for (int i = 0; i < current.Subdirectories.Count; i++)
                    stack.Push(current.Subdirectories[i]);
            }
        }

        return maxEnd;
    }

    /// <summary>
    /// Recursively trims over-allocated List backing arrays after scanning completes.
    /// Reclaims ~50% of wasted capacity from List doubling growth strategy.
    /// </summary>
    public void Compact()
    {
        Stack<DirectoryNode> stack = new Stack<DirectoryNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            DirectoryNode current = stack.Pop();
            lock (current.Subdirectories)
            {
                if (current.Subdirectories.Count == 0)
                    continue;

                current.Subdirectories.TrimExcess();
                for (int i = 0; i < current.Subdirectories.Count; i++)
                    stack.Push(current.Subdirectories[i]);
            }
        }
    }

    private string MaterializePath()
    {
        int count = 0;
        DirectoryNode? cursor = this;
        string? basePath = null;
        while (cursor != null)
        {
            basePath = Volatile.Read(ref cursor._path);
            if (basePath != null)
                break;
            count++;
            cursor = cursor.Parent;
        }

        if (count == 0)
            return basePath!;

        DirectoryNode[] chain = ArrayPool<DirectoryNode>.Shared.Rent(count);
        try
        {
            cursor = this;
            for (int i = count - 1; i >= 0; i--)
            {
                chain[i] = cursor!;
                cursor = cursor!.Parent;
            }

            int totalLen = basePath?.Length ?? 0;
            char sep = System.IO.Path.DirectorySeparatorChar;
            bool prevEndsWithSep = basePath != null && basePath.Length > 0 &&
                (basePath[^1] == System.IO.Path.DirectorySeparatorChar ||
                 basePath[^1] == System.IO.Path.AltDirectorySeparatorChar ||
                 (OperatingSystem.IsWindows() && basePath.Length == 2 && basePath[1] == ':'));

            bool needSep = !string.IsNullOrEmpty(basePath) && !prevEndsWithSep;
            for (int i = 0; i < count; i++)
            {
                string seg = chain[i].Name;
                if (needSep)
                    totalLen++;
                totalLen += seg.Length;
                needSep = seg.Length > 0 &&
                    seg[^1] != System.IO.Path.DirectorySeparatorChar &&
                    seg[^1] != System.IO.Path.AltDirectorySeparatorChar;
            }

            string built = string.Create(totalLen, (basePath, chain, count, prevEndsWithSep, sep), static (dest, state) =>
            {
                int pos = 0;
                if (!string.IsNullOrEmpty(state.basePath))
                {
                    state.basePath.CopyTo(dest);
                    pos = state.basePath.Length;
                }

                bool needSepBeforeNext = !string.IsNullOrEmpty(state.basePath) && !state.prevEndsWithSep;
                for (int i = 0; i < state.count; i++)
                {
                    string seg = state.chain[i].Name;
                    if (needSepBeforeNext)
                        dest[pos++] = state.sep;
                    seg.CopyTo(dest[pos..]);
                    pos += seg.Length;
                    needSepBeforeNext = seg.Length > 0 &&
                        seg[^1] != System.IO.Path.DirectorySeparatorChar &&
                        seg[^1] != System.IO.Path.AltDirectorySeparatorChar;
                }
            });

            string? existing = Interlocked.CompareExchange(ref _path, built, null);
            return existing ?? built;
        }
        finally
        {
            Array.Clear(chain, 0, count);
            ArrayPool<DirectoryNode>.Shared.Return(chain);
        }
    }

    /// <summary>
    /// Exact path bytes for native calls. Rebuilt iteratively from the parent chain without materializing child Path strings.
    /// </summary>
    internal byte[] GetNativePath(bool nullTerminated = true)
    {
        if (!HasNativeAncestry && Volatile.Read(ref _path) is string cachedPath)
        {
            int length = Encoding.UTF8.GetByteCount(cachedPath);
            byte[] bytes = new byte[nullTerminated ? length + 1 : length];
            Encoding.UTF8.GetBytes(cachedPath, bytes);
            return bytes;
        }

        int count = 0;
        DirectoryNode? cursor = this;
        string? basePath = null;
        byte[]? baseBytes = null;
        while (cursor != null)
        {
            if (cursor.Parent == null || !cursor.HasNativeAncestry)
            {
                if (cursor._nativeRootPath != null)
                {
                    baseBytes = cursor._nativeRootPath;
                    break;
                }

                string? maybePath = Volatile.Read(ref cursor._path);
                if (maybePath != null)
                {
                    basePath = maybePath;
                    break;
                }
            }

            count++;
            cursor = cursor.Parent;
        }

        if (count == 0)
        {
            int baseLen = baseBytes?.Length ?? Encoding.UTF8.GetByteCount(basePath!);
            byte[] exact = new byte[nullTerminated ? baseLen + 1 : baseLen];
            if (baseBytes != null)
                baseBytes.CopyTo(exact, 0);
            else
                Encoding.UTF8.GetBytes(basePath!, exact);
            return exact;
        }

        DirectoryNode[] chain = ArrayPool<DirectoryNode>.Shared.Rent(count);
        try
        {
            cursor = this;
            for (int i = count - 1; i >= 0; i--)
            {
                chain[i] = cursor!;
                cursor = cursor!.Parent;
            }

            int baseLen = baseBytes != null
                ? baseBytes.Length
                : basePath != null ? Encoding.UTF8.GetByteCount(basePath) : 0;

            bool endsWithSep;
            if (baseBytes != null)
            {
                endsWithSep = baseBytes.Length > 0 && (baseBytes[^1] == (byte)'/' || baseBytes[^1] == (byte)'\\');
            }
            else if (!string.IsNullOrEmpty(basePath))
            {
                endsWithSep = basePath[^1] == '/' || basePath[^1] == '\\';
            }
            else
            {
                endsWithSep = true;
            }

            int totalBytes = baseLen;
            bool needSep = baseLen > 0 && !endsWithSep;
            for (int i = 0; i < count; i++)
            {
                DirectoryNode segNode = chain[i];
                int segLen = segNode.NativeName != null
                    ? segNode.NativeName.Length
                    : Encoding.UTF8.GetByteCount(segNode.Name);
                if (needSep)
                    totalBytes++;
                totalBytes += segLen;

                bool endsInSep = segNode.NativeName != null
                    ? segNode.NativeName.Length > 0 && (segNode.NativeName[^1] == (byte)'/' || segNode.NativeName[^1] == (byte)'\\')
                    : segNode.Name.Length > 0 && (segNode.Name[^1] == '/' || segNode.Name[^1] == '\\');
                needSep = segLen > 0 && !endsInSep;
            }

            byte[] result = new byte[nullTerminated ? totalBytes + 1 : totalBytes];
            int pos = 0;
            if (baseBytes != null)
            {
                baseBytes.CopyTo(result, 0);
                pos = baseBytes.Length;
            }
            else if (!string.IsNullOrEmpty(basePath))
            {
                pos = Encoding.UTF8.GetBytes(basePath, result);
            }

            byte sepByte = (byte)System.IO.Path.DirectorySeparatorChar;
            needSep = baseLen > 0 && !endsWithSep;
            for (int i = 0; i < count; i++)
            {
                DirectoryNode segNode = chain[i];
                if (needSep)
                    result[pos++] = sepByte;

                int written;
                if (segNode.NativeName != null)
                {
                    segNode.NativeName.CopyTo(result, pos);
                    written = segNode.NativeName.Length;
                }
                else
                {
                    written = Encoding.UTF8.GetBytes(segNode.Name, result.AsSpan(pos));
                }

                pos += written;
                byte lastByte = pos > 0 ? result[pos - 1] : (byte)0;
                needSep = written > 0 && lastByte != (byte)'/' && lastByte != (byte)'\\';
            }

            return result;
        }
        finally
        {
            Array.Clear(chain, 0, count);
            ArrayPool<DirectoryNode>.Shared.Return(chain);
        }
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

    internal void ApplyInlineDelta(long sizeDelta, int subdirsDelta, int filesDelta)
    {
        if (sizeDelta != 0) Volatile.Write(ref _totalSize, _totalSize + sizeDelta);
        if (subdirsDelta != 0) Volatile.Write(ref _subdirectoryCount, _subdirectoryCount + subdirsDelta);
        if (filesDelta != 0) Volatile.Write(ref _fileCount, _fileCount + filesDelta);
    }
}