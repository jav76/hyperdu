using System.Buffers;
using System.Text;
using hyperdu.Core.Du;
using hyperdu.Core.Models;
using hyperdu.Core.Native;

namespace hyperdu.Core.Scanning;

public static class DirectoryListing
{
    /// <summary>
    /// Lists the non-directory entries directly inside <paramref name="directory"/> with their sizes,
    /// following symlinks for the size. Throws when the directory cannot be read.
    /// </summary>
    public static List<(string Name, long Size)> ListFiles(
        DirectoryNode directory,
        bool useNativeIo = true,
        bool apparentSize = false)
    {
        return ListFilesCore(directory, useNativeIo, apparentSize, skipHidden: false, excludeMatcher: null, excludedPaths: null);
    }

    /// <summary>
    /// Lists the non-directory entries directly inside <paramref name="directory"/> applying scanner options
    /// for native/managed I/O, apparent vs allocated size, hidden filtering, and path/glob exclusions.
    /// </summary>
    public static List<(string Name, long Size)> ListFiles(DirectoryNode directory, ScanOptions? options)
    {
        return ListFilesCore(
            directory,
            options?.UseNativeIo ?? true,
            options?.ApparentSize ?? false,
            options?.SkipHidden ?? false,
            options?.ExcludeMatcher is { IsEmpty: false } matcher ? matcher : null,
            options?.ExcludedPaths is { Count: > 0 } paths ? paths : null);
    }

    private static List<(string Name, long Size)> ListFilesCore(
        DirectoryNode directory,
        bool useNativeIo,
        bool apparentSize,
        bool skipHidden,
        ExcludeMatcher? excludeMatcher,
        IReadOnlyList<string>? excludedPaths)
    {
        return useNativeIo && NativeIo.IsSupported
            ? ListFilesNative(directory, apparentSize, skipHidden, excludeMatcher, excludedPaths)
            : ListFilesManaged(directory, skipHidden, excludeMatcher, excludedPaths);
    }

    private static List<(string Name, long Size)> ListFilesManaged(
        DirectoryNode directory,
        bool skipHidden,
        ExcludeMatcher? excludeMatcher,
        IReadOnlyList<string>? excludedPaths)
    {
        List<(string Name, long Size)> files = new List<(string Name, long Size)>();
        DirectoryInfo dirInfo = new DirectoryInfo(directory.Path);
        EnumerationOptions enumOpts = new EnumerationOptions
        {
            AttributesToSkip = skipHidden ? FileAttributes.Hidden | FileAttributes.System : 0
        };
        ParallelScanner.PrepareLegacyExcludes(excludedPaths, out HashSet<string>? excludedExact, out List<string>? excludedPrefixes);
        foreach (FileInfo file in dirInfo.EnumerateFiles("*", enumOpts))
        {
            if (excludeMatcher != null && excludeMatcher.IsExcludedManagedPath(file.FullName))
                continue;
            if (ParallelScanner.MatchesLegacyExcludedPaths(file.FullName, excludedExact, excludedPrefixes))
                continue;

            long size = 0;
            try
            {
                size = file.Length;
            }
            catch
            {
                // Ignore exception when querying size of specific files (e.g. system files).
            }

            files.Add((file.Name, size));
        }

        return files;
    }

    private static List<(string Name, long Size)> ListFilesNative(
        DirectoryNode directory,
        bool apparentSize,
        bool skipHidden,
        ExcludeMatcher? excludeMatcher,
        IReadOnlyList<string>? excludedPaths)
    {
        List<(string Name, long Size)> files = new List<(string Name, long Size)>();
        Span<byte> statBuffer = stackalloc byte[NativeIo.StatBufferSize];
        bool checkDotHidden = skipHidden && !NativeIo.IsWindows;
        bool checkHiddenFlag = skipHidden && (NativeIo.IsMacOS || NativeIo.IsWindows);

        byte[]? parentNativePath = excludeMatcher != null ? directory.GetNativePath(nullTerminated: false) : null;
        byte[]? pathScratch = excludeMatcher != null
            ? ArrayPool<byte>.Shared.Rent(Math.Max(1024, parentNativePath!.Length + 256))
            : null;
        ParallelScanner.PrepareLegacyExcludes(excludedPaths, out HashSet<string>? excludedExact, out List<string>? excludedPrefixes);

        try
        {
            NativeDirectory? handle = NativeDirectory.OpenPathSafe(directory.GetNativePath(), out int openError, followSymlinks: true);
            if (handle == null) throw new IOException(NativeIo.DescribeError(openError));

            using (handle)
            {
                while (handle.MoveNext())
                {
                    ReadOnlySpan<byte> name = handle.CurrentName;
                    if (checkDotHidden && name[0] == (byte)'.') continue;

                    if (excludeMatcher != null && pathScratch != null)
                    {
                        int len = DuAccounting.WriteChildPathIntoPooled(parentNativePath!, name, ref pathScratch);
                        if (excludeMatcher.IsExcluded(pathScratch.AsSpan(0, len))) continue;
                    }

                    if (excludedExact != null && excludedPrefixes != null &&
                        ParallelScanner.MatchesLegacyExcludedPaths(
                            Path.Combine(directory.Path, Encoding.UTF8.GetString(name)),
                            excludedExact,
                            excludedPrefixes))
                    {
                        continue;
                    }

                    if (handle.CurrentKind == NativeEntryKind.Directory) continue;

                    if (checkHiddenFlag && handle.TryStatCurrent(false, statBuffer, out NativeStat linkStat) &&
                        linkStat.HasHiddenFlag)
                    {
                        continue;
                    }

                    bool hasStat = handle.TryStatCurrent(true, statBuffer, out NativeStat stat);
                    if (hasStat && stat.Kind == NativeEntryKind.Directory) continue;

                    long size = hasStat ? NativeIo.ComputeEntryByteSize(in stat, apparentSize) : 0;
                    files.Add((Encoding.UTF8.GetString(name), size));
                }
            }
        }
        finally
        {
            if (pathScratch != null)
                ArrayPool<byte>.Shared.Return(pathScratch);
        }

        return files;
    }
}
