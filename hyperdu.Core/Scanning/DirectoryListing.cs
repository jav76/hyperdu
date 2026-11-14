using System.Text;
using hyperdu.Core.Models;
using hyperdu.Core.Native;

namespace hyperdu.Core.Scanning;

public static class DirectoryListing
{
    /// <summary>
    /// Lists the non-directory entries directly inside <paramref name="directory"/> with their sizes,
    /// following symlinks for the size. Throws when the directory cannot be read.
    /// </summary>
    public static List<(string Name, long Size)> ListFiles(DirectoryNode directory, bool useNativeIo = true)
    {
        return useNativeIo && NativeIo.IsSupported ? ListFilesNative(directory) : ListFilesManaged(directory);
    }

    private static List<(string Name, long Size)> ListFilesManaged(DirectoryNode directory)
    {
        List<(string Name, long Size)> files = new List<(string Name, long Size)>();
        DirectoryInfo dirInfo = new DirectoryInfo(directory.Path);
        foreach (FileInfo file in dirInfo.EnumerateFiles())
        {
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

    private static unsafe List<(string Name, long Size)> ListFilesNative(DirectoryNode directory)
    {
        List<(string Name, long Size)> files = new List<(string Name, long Size)>();
        byte* statBuffer = stackalloc byte[NativeIo.StatBufferSize];

        NativeDirectory? handle;
        int openError;
        fixed (byte* path = directory.GetNativePath())
        {
            handle = NativeDirectory.Open(path, out openError);
        }

        if (handle == null) throw new IOException(NativeIo.DescribeError(openError));

        using (handle)
        {
            while (handle.MoveNext())
            {
                if (handle.CurrentKind == NativeEntryKind.Directory) continue;

                bool hasStat = handle.TryStatCurrent(true, statBuffer, out NativeStat stat);
                if (hasStat && stat.Kind == NativeEntryKind.Directory) continue;

                files.Add((Encoding.UTF8.GetString(handle.CurrentName), hasStat ? stat.Size : 0));
            }
        }

        return files;
    }
}
