using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace hyperdu.Core.Models;

public class DirectoryNode
{
    public string Path { get; }
    public string Name { get; }
    public DirectoryNode? Parent { get; }
    public long SelfSize { get; set; }

    internal long _totalSize;
    public long TotalSize
    {
        get => Volatile.Read(ref _totalSize);
        set => Volatile.Write(ref _totalSize, value);
    }

    internal int _subdirectoryCount;
    public int SubdirectoryCount
    {
        get => Volatile.Read(ref _subdirectoryCount);
        set => Volatile.Write(ref _subdirectoryCount, value);
    }

    internal int _fileCount;
    public int FileCount
    {
        get => Volatile.Read(ref _fileCount);
        set => Volatile.Write(ref _fileCount, value);
    }

    public List<DirectoryNode> Subdirectories { get; } = new();
    public List<FileNode> Files { get; } = new();
    public string? ErrorMessage { get; set; }

    public bool IsScanned { get; set; }

    private long _startTicks;
    public long StartTicks
    {
        get => Volatile.Read(ref _startTicks);
        set => Volatile.Write(ref _startTicks, value);
    }

    private long _endTicks;
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
        foreach (var sub in subs)
        {
            long subMax = sub.GetMaxEndTicksRecursive();
            if (subMax > maxEnd)
            {
                maxEnd = subMax;
            }
        }
        return maxEnd;
    }

    public DirectoryNode(string path, DirectoryNode? parent = null)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
        Parent = parent;
    }
}

public record FileNode(string Name, long Size);
