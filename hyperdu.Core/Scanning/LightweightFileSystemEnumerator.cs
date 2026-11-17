using System;
using System.IO;
using System.IO.Enumeration;

namespace hyperdu.Core.Scanning;

public class LightweightFileSystemEnumerator : FileSystemEnumerator<LightweightEntry>
{
    internal delegate bool EntryExcludePredicate(ReadOnlySpan<char> directory, ReadOnlySpan<char> fileName);

    private readonly EntryExcludePredicate? _isExcluded;

    public LightweightFileSystemEnumerator(string directory, EnumerationOptions options)
        : this(directory, options, null)
    {
    }

    internal LightweightFileSystemEnumerator(
        string directory,
        EnumerationOptions options,
        EntryExcludePredicate? isExcluded)
        : base(directory, options)
    {
        _isExcluded = isExcluded;
    }

    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
    {
        // FileSystemEnumerator handles AttributesToSkip and "." / ".." automatically.
        return _isExcluded == null || !_isExcluded(entry.Directory, entry.FileName);
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
