using System;
using System.IO;
using System.IO.Enumeration;

namespace hyperdu.Core.Scanning;

public class LightweightFileSystemEnumerator : FileSystemEnumerator<LightweightEntry>
{
    public LightweightFileSystemEnumerator(string directory, EnumerationOptions options)
        : base(directory, options)
    {
    }

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
    {
        // FileSystemEnumerator handles AttributesToSkip and "." / ".." automatically.
        return true;
    }

    protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
    {
        // ParallelScanner manages queueing and traversal. Do not recurse automatically.
        return false;
    }

    protected override LightweightEntry TransformEntry(ref FileSystemEntry entry)
    {
        // Only materialize name string for directories.
        // For files, set it to string.Empty to prevent millions of string allocations.
        string name = entry.IsDirectory ? entry.FileName.ToString() : string.Empty;

        return new LightweightEntry
        {
            Name = name,
            IsDirectory = entry.IsDirectory,
            Length = entry.Length,
            Attributes = entry.Attributes
        };
    }
}
