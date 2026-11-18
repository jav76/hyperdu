using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using hyperdu.Cli;
using hyperdu.Core.Du;
using hyperdu.Core.Models;
using hyperdu.Core.Native;
using hyperdu.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace hyperdu.Tests;

public sealed partial class WindowsNativeTests
{
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;

    [WindowsFact]
    public void BackendDetection_SelectsWindowsNtQueryAndNotPosix()
    {
        Assert.True(NativeIo.IsSupported);
        Assert.True(NativeIo.IsWindows);
        Assert.False(NativeIo.IsPosix);
        Assert.True(NativeIo.UsesWindowsNtQuery);
        Assert.False(NativeIo.UsesGetdents64);
    }

    [WindowsFact]
    public async Task SparseFile_ReportsTrueAllocatedBlocksVsApparentBytesAcrossAllEngines()
    {
        using TempTree tree = new();
        string sparsePath = Path.Combine(tree.Root, "sparse.bin");
        const long logicalSize = 4L * 1024 * 1024;
        CreateSparseFile(sparsePath, logicalSize, writeHeadAndTailChunks: true);

        NativeStat pathStat = StatPathOrThrow(sparsePath, followSymlinks: false);
        long allocBytes = pathStat.Blocks * 512L;
        Assert.Equal(NativeEntryKind.File, pathStat.Kind);
        Assert.Equal(logicalSize, pathStat.Size);
        Assert.InRange(allocBytes, 1L, 512L * 1024);
        Assert.NotEqual(0UL, pathStat.Device);
        Assert.NotEqual(0UL, pathStat.Inode);

        NativeStat inlineStat = ReadSingleInlineStat(tree.Root, "sparse.bin");
        Assert.Equal(pathStat.Size, inlineStat.Size);
        Assert.Equal(pathStat.Blocks, inlineStat.Blocks);
        Assert.Equal(pathStat.Device, inlineStat.Device);
        Assert.Equal(pathStat.Inode, inlineStat.Inode);

        ParallelScanner apparentScanner = new(new ScanOptions
        {
            WorkerCount = 2,
            UseNativeIo = true,
            ApparentSize = true
        });
        DirectoryNode apparentRoot = await apparentScanner.ScanAsync(tree.Root);
        Assert.Null(apparentRoot.ErrorMessage);
        Assert.Equal(logicalSize, apparentRoot.TotalSize);

        ParallelScanner allocScanner = new(new ScanOptions
        {
            WorkerCount = 2,
            UseNativeIo = true,
            ApparentSize = false
        });
        DirectoryNode allocRoot = await allocScanner.ScanAsync(tree.Root);
        Assert.Null(allocRoot.ErrorMessage);
        Assert.Equal(allocBytes, allocRoot.TotalSize);

        List<(string Name, long Size)> listedAlloc = DirectoryListing.ListFiles(allocRoot, useNativeIo: true, apparentSize: false);
        Assert.Equal([("sparse.bin", allocBytes)], listedAlloc);

        // Verify both SequentialWalker (single-file/parallel parity via DuCommand) and ParallelDuWalker.
        (int rcApparent, string outApparent, string errApparent) = RunDu(["-b", "-s", tree.Root], "du.exe");
        Assert.Equal(0, rcApparent);
        Assert.Empty(errApparent);
        Assert.Equal($"{logicalSize}\t{tree.Root}\n", outApparent);

        (int rcAlloc, string outAlloc, string errAlloc) = RunDu(["-B1", "-s", tree.Root], "du.exe");
        Assert.Equal(0, rcAlloc);
        Assert.Empty(errAlloc);
        Assert.Equal($"{allocBytes}\t{tree.Root}\n", outAlloc);

        (int rcSeqAlloc, string outSeqAlloc, string errSeqAlloc) = RunSequentialDu(["-B1", "-s", tree.Root], "du.exe");
        Assert.Equal(0, rcSeqAlloc);
        Assert.Empty(errSeqAlloc);
        Assert.Equal(outAlloc, outSeqAlloc);
    }

    [WindowsFact]
    public async Task HardLinks_DeduplicateByVolumeSerialAndFileIdAcrossDirsAndOperands()
    {
        using TempTree tree = new();
        string orig = tree.File("a/orig.bin", 65_536);
        Directory.CreateDirectory(Path.Combine(tree.Root, "b"));
        Directory.CreateDirectory(Path.Combine(tree.Root, "c"));
        string link1 = Path.Combine(tree.Root, "b", "link1.bin");
        string link2 = Path.Combine(tree.Root, "c", "link2.bin");

        Assert.True(CreateHardLinkW(link1, orig, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);
        Assert.True(CreateHardLinkW(link2, orig, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);

        NativeStat s0 = StatPathOrThrow(orig, followSymlinks: false);
        NativeStat s1 = StatPathOrThrow(link1, followSymlinks: false);
        NativeStat s2 = StatPathOrThrow(link2, followSymlinks: false);
        Assert.Equal(3u, s0.HardLinks);
        Assert.Equal(s0.Device, s1.Device);
        Assert.Equal(s0.Inode, s1.Inode);
        Assert.Equal(s0.Device, s2.Device);
        Assert.Equal(s0.Inode, s2.Inode);

        // Inline NtQueryDirectoryFile stats omit nNumberOfLinks (HardLinks == 0) but retain Device + Inode.
        NativeStat inline1 = ReadSingleInlineStat(Path.Combine(tree.Root, "b"), "link1.bin");
        Assert.Equal(0u, inline1.HardLinks);
        Assert.Equal(s0.Device, inline1.Device);
        Assert.Equal(s0.Inode, inline1.Inode);

        ParallelScanner dedupScanner = new(new ScanOptions
        {
            WorkerCount = 4,
            UseNativeIo = true,
            ApparentSize = true,
            CountLinks = false
        });
        DirectoryNode dedupRoot = await dedupScanner.ScanAsync(tree.Root);
        Assert.Equal(65_536L, dedupRoot.TotalSize);
        Assert.Equal(1, dedupScanner.FilesScanned);

        ParallelScanner countAllScanner = new(new ScanOptions
        {
            WorkerCount = 4,
            UseNativeIo = true,
            ApparentSize = true,
            CountLinks = true
        });
        DirectoryNode countAllRoot = await countAllScanner.ScanAsync(tree.Root);
        Assert.Equal(3L * 65_536L, countAllRoot.TotalSize);
        Assert.Equal(3, countAllScanner.FilesScanned);

        string dirA = Path.Combine(tree.Root, "a");
        string dirB = Path.Combine(tree.Root, "b");
        (int rcDedupe, string outDedupe, string errDedupe) = RunDu(["-b", "-s", dirA, dirB], "du.exe");
        Assert.Equal(0, rcDedupe);
        Assert.Empty(errDedupe);
        Assert.Equal($"65536\t{dirA}\n0\t{dirB}\n", outDedupe);

        (int rcCountLinks, string outCountLinks, string errCountLinks) = RunDu(["-b", "-s", "-l", dirA, dirB], "du.exe");
        Assert.Equal(0, rcCountLinks);
        Assert.Empty(errCountLinks);
        Assert.Equal($"65536\t{dirA}\n65536\t{dirB}\n", outCountLinks);

        // Verify cross-volume identity separation even when FileId matches across volumes.
        Assert.NotEqual(new FileIdentity(s0.Device, s0.Inode), new FileIdentity(s0.Device + 1, s0.Inode));
    }

    [WindowsFact]
    public async Task ReparsePointsAndJunctions_RespectP_H_L_HandleCycles_AndDanglingTargets()
    {
        Assert.True(Win32.IsNameSurrogateReparseTag(0xA0000003));  // IO_REPARSE_TAG_MOUNT_POINT
        Assert.True(Win32.IsNameSurrogateReparseTag(0xA000000C));  // IO_REPARSE_TAG_SYMLINK
        Assert.False(Win32.IsNameSurrogateReparseTag(0x9000101A)); // Cloud placeholder
        Assert.False(Win32.IsNameSurrogateReparseTag(0x80000017)); // WOF compressed

        using TempTree tree = new();
        string targetDir = tree.Dir("real_target");
        tree.File("real_target/payload.bin", 32_768);
        tree.File("direct.bin", 4_096);

        string linksDir = tree.Dir("links");
        string juncPath = Path.Combine(linksDir, "junc");
        CreateJunction(juncPath, targetDir);

        string brokenHolder = tree.Dir("broken_holder");
        string brokenTarget = Path.Combine(tree.Root, "ephemeral_target");
        Directory.CreateDirectory(brokenTarget);
        string brokenJunc = Path.Combine(brokenHolder, "broken_junc");
        CreateJunction(brokenJunc, brokenTarget);
        Directory.Delete(brokenTarget);

        // Junction cycle: links/cycle/back -> links/cycle
        string cycleDir = tree.Dir("links/cycle");
        tree.File("links/cycle/leaf.bin", 1_024);
        string cycleJunc = Path.Combine(cycleDir, "back");
        CreateJunction(cycleJunc, cycleDir);

        // Optional real NTFS symlink if Windows Developer Mode or privilege permits.
        string? symPath = TryCreateDirectorySymlink(Path.Combine(linksDir, "sym"), targetDir);

        NativeStat inlineJunc = ReadSingleInlineStat(linksDir, "junc");
        Assert.Equal(NativeEntryKind.Symlink, inlineJunc.Kind);
        NativeStat inlineBroken = ReadSingleInlineStat(brokenHolder, "broken_junc");
        Assert.Equal(NativeEntryKind.Symlink, inlineBroken.Kind);

        NativeStat lstatJunc = StatPathOrThrow(juncPath, followSymlinks: false);
        Assert.Equal(NativeEntryKind.Symlink, lstatJunc.Kind);
        NativeStat statJunc = StatPathOrThrow(juncPath, followSymlinks: true);
        Assert.Equal(NativeEntryKind.Directory, statJunc.Kind);

        // TUI default (-P): junctions/symlinks are not traversed.
        ParallelScanner scanP = new(new ScanOptions { WorkerCount = 2, UseNativeIo = true, FollowSymlinks = false, ApparentSize = true });
        DirectoryNode rootP = await scanP.ScanAsync(tree.Root);
        Assert.Equal(32_768L + 4_096L + 1_024L, rootP.TotalSize);

        // du -P (default): does not follow junc/cycle/broken.
        (int rcP, string outP, string errP) = RunDu(["-b", "-s", tree.Root], "du.exe");
        Assert.Equal(0, rcP);
        Assert.Empty(errP);
        Assert.Equal($"{32_768L + 4_096L + 1_024L}\t{tree.Root}\n", outP);

        // du -H: dereferences command-line junction operand, does not recurse into nested links/cycle/back.
        (int rcH, string outH, string errH) = RunDu(["-H", "-b", "-s", juncPath], "du.exe");
        Assert.Equal(0, rcH);
        Assert.Empty(errH);
        Assert.Equal($"32768\t{juncPath}\n", outH);

        // du -H on links/cycle: dereferences operand (already a real dir) and does NOT follow back.
        (int rcHCycle, string outHCycle, string errHCycle) = RunDu(["-H", "-b", "-s", cycleDir], "du.exe");
        Assert.Equal(0, rcHCycle);
        Assert.Empty(errHCycle);
        Assert.Equal($"1024\t{cycleDir}\n", outHCycle);

        // du -L: follows junctions cleanly without infinite loop or double-counting shared targets.
        (int rcL, string outL, string errL) = RunDu(["-L", "-b", "-s", linksDir], "du.exe");
        Assert.Equal(0, rcL);
        Assert.Empty(errL);
        // targetDir is reached via junc (and optional sym); FileIdentity dedupes real_target so payload.bin is counted once!
        Assert.Equal($"{32_768L + 1_024L}\t{linksDir}\n", outL);

        (int rcSeqL, string outSeqL, string errSeqL) = RunSequentialDu(["-L", "-b", "-s", linksDir], "du.exe");
        Assert.Equal(0, rcSeqL);
        Assert.Empty(errSeqL);
        Assert.Equal(outL, outSeqL);

        // du -L on a dangling junction reports cannot access and exits 1 (GNU du -L parity).
        (int rcBroken, string outBroken, string errBroken) = RunDu(["-L", "-b", "-s", brokenHolder], "du.exe");
        Assert.Equal(1, rcBroken);
        Assert.Equal($"du.exe: cannot access '{brokenJunc}'\n", errBroken);
        Assert.Equal($"0\t{brokenHolder}\n", outBroken);

        // Confirm optional symlink was classified identically as Symlink when created.
        if (symPath != null)
        {
            NativeStat symInline = ReadSingleInlineStat(linksDir, "sym");
            Assert.Equal(NativeEntryKind.Symlink, symInline.Kind);
        }
    }

    [WindowsFact]
    public async Task DeepTreeBeyondMaxPath_TraversesRescansListsAndRunsDuCleanly()
    {
        using TempTree tree = new();
        List<string> createdDirs = [];
        string leafFile = "";

        try
        {
            string cursor = tree.Root;
            for (int i = 0; i < 45; i++)
            {
                cursor = Path.Combine(cursor, $"segment_level_{i:D2}_pad0123");
                string extCursor = ToExtendedPath(cursor);
                Assert.True(CreateDirectoryW(extCursor, IntPtr.Zero), new Win32Exception(Marshal.GetLastWin32Error()).Message);
                createdDirs.Add(cursor);
            }

            Assert.True(cursor.Length > 1000, $"Expected deep path > 1000 chars, got {cursor.Length}");
            leafFile = Path.Combine(cursor, "leaf_payload.bin");

            using (SafeFileHandle handle = CreateFileW(
                ToExtendedPath(leafFile),
                GenericWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                CreateAlways,
                FileAttributeNormal,
                IntPtr.Zero))
            {
                Assert.False(handle.IsInvalid, new Win32Exception(Marshal.GetLastWin32Error()).Message);
                using FileStream fs = new(handle, FileAccess.Write);
                byte[] data = new byte[16_384];
                data.AsSpan().Fill(0x42);
                await fs.WriteAsync(data);
                fs.Flush(flushToDisk: true);
            }

            // Pass un-prefixed > 1000 char path to public APIs; WindowsIo must handle \\?\ internally.
            Assert.True(ParallelScanner.DirectoryExistsPathSafe(cursor));
            using (NativeDirectory? dir = NativeDirectory.OpenPathSafe(Encoding.UTF8.GetBytes(cursor + "\0"), out int openErr))
            {
                Assert.NotNull(dir);
                Assert.Equal(0, openErr);
            }

            ParallelScanner scanner = new(new ScanOptions { WorkerCount = 4, UseNativeIo = true, ApparentSize = true });
            DirectoryNode root = await scanner.ScanAsync(tree.Root);
            Assert.Null(root.ErrorMessage);
            Assert.Equal(16_384L, root.TotalSize);
            Assert.Equal(1, scanner.FilesScanned);

            DirectoryNode node = root;
            for (int i = 0; i < 45; i++)
            {
                Assert.Null(node.ErrorMessage);
                node = node.Subdirectories.Single();
            }

            Assert.True(node.Path.Length > 1000);
            Assert.Null(node.ErrorMessage);
            Assert.Equal(16_384L, node.TotalSize);

            List<(string Name, long Size)> leafListed = DirectoryListing.ListFiles(node, useNativeIo: true, apparentSize: true);
            Assert.Equal([("leaf_payload.bin", 16_384L)], leafListed);

            DirectoryNode rescanned = await scanner.ScanAsync(node);
            Assert.Null(rescanned.ErrorMessage);
            Assert.Equal(16_384L, rescanned.TotalSize);

            (int rcRoot, string outRoot, string errRoot) = RunDu(["-b", "-s", tree.Root], "du.exe");
            Assert.Equal(0, rcRoot);
            Assert.Empty(errRoot);
            Assert.Equal($"16384\t{tree.Root}\n", outRoot);

            (int rcDeep, string outDeep, string errDeep) = RunDu(["-b", "-s", cursor], "du.exe");
            Assert.Equal(0, rcDeep);
            Assert.Empty(errDeep);
            Assert.Equal($"16384\t{cursor}\n", outDeep);

            (int rcSeqRoot, string outSeqRoot, string errSeqRoot) = RunSequentialDu(["-b", "-s", tree.Root], "du.exe");
            Assert.Equal(0, rcSeqRoot);
            Assert.Empty(errSeqRoot);
            Assert.Equal(outRoot, outSeqRoot);
        }
        finally
        {
            if (!string.IsNullOrEmpty(leafFile))
                _ = DeleteFileW(ToExtendedPath(leafFile));
            for (int i = createdDirs.Count - 1; i >= 0; i--)
                _ = RemoveDirectoryW(ToExtendedPath(createdDirs[i]));
        }
    }

    [WindowsFact]
    public async Task LargeDirectoryPagination_Timestamps_HiddenAttributes_AndExcludesWorkWithNtQuery()
    {
        using TempTree tree = new();

        // > 1,200 entries ensures NtQueryDirectoryFile spans multiple 64 KB buffer pages cleanly.
        string bulkDir = tree.Dir("bulk");
        for (int i = 0; i < 1_200; i++)
            tree.File($"bulk/item_{i:D4}.dat", 64);

        string stamped = tree.File("stamped.bin", 512);
        DateTime mtime = new(2020, 6, 15, 12, 34, 56, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(stamped, mtime);

        string dotVisible = tree.File(".dotvisible", 128);
        string attrHiddenFile = tree.File("attr_hidden.bin", 256);
        File.SetAttributes(attrHiddenFile, FileAttributes.Hidden);
        string hiddenDir = tree.Dir("hidden_dir");
        tree.File("hidden_dir/inside.bin", 1_024);
        File.SetAttributes(hiddenDir, FileAttributes.Directory | FileAttributes.Hidden);

        NativeStat stampedStat = StatPathOrThrow(stamped, followSymlinks: false);
        Assert.Equal(1592224496L, stampedStat.MtimeSec);
        Assert.Equal(1592224496L, ReadSingleInlineStat(tree.Root, "stamped.bin").MtimeSec);

        ScanOptions visibleOpts = new()
        {
            WorkerCount = 4,
            UseNativeIo = true,
            ApparentSize = true,
            SkipHidden = true
        };
        ParallelScanner visibleScanner = new(visibleOpts);
        DirectoryNode visibleRoot = await visibleScanner.ScanAsync(tree.Root);
        Assert.Equal(1_200L * 64L + 512L + 128L, visibleRoot.TotalSize);
        Assert.DoesNotContain(DirectoryListing.ListFiles(visibleRoot, visibleOpts),
            f => f.Name == "attr_hidden.bin");
        Assert.Contains(DirectoryListing.ListFiles(visibleRoot, visibleOpts),
            f => f.Name == ".dotvisible");
        Assert.DoesNotContain(visibleRoot.Subdirectories, d => d.Name == "hidden_dir");

        // Verify dotfile was NOT marked hidden by Windows attributes.
        Assert.False(StatPathOrThrow(dotVisible, followSymlinks: false).HasHiddenFlag);
        Assert.True(StatPathOrThrow(attrHiddenFile, followSymlinks: false).HasHiddenFlag);

        // Verify backslash exclude pattern + Windows case-insensitive du.exe persona.
        Assert.Equal(DuDialect.Gnu, DuOptions.ResolveDialect(@"C:\Tools\DU.EXE"));
        (int rcExcl, string outExcl, string errExcl) = RunDu(
            ["-b", "-s", @"--exclude=bulk\*", "--exclude=hidden_dir", "--exclude=attr_hidden.bin", tree.Root],
            "du.exe");
        Assert.Equal(0, rcExcl);
        Assert.Empty(errExcl);
        Assert.Equal($"{512L + 128L}\t{tree.Root}\n", outExcl);
    }

    private static string ToExtendedPath(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }

    private static NativeStat StatPathOrThrow(string path, bool followSymlinks)
    {
        byte[] nt = Encoding.UTF8.GetBytes(path + "\0");
        Span<byte> scratch = stackalloc byte[NativeIo.StatBufferSize];
        Assert.True(
            NativeIo.TryStatPath(nt, followSymlinks, scratch, out NativeStat stat, out int errno),
            $"TryStatPath failed for '{path}' with errno {errno} ({NativeIo.DescribeError(errno)})");
        return stat;
    }

    private static NativeStat ReadSingleInlineStat(string dirPath, string entryName)
    {
        byte[] nt = Encoding.UTF8.GetBytes(dirPath + "\0");
        using NativeDirectory? dir = NativeDirectory.OpenPathSafe(nt, out int errno);
        Assert.NotNull(dir);
        Assert.Equal(0, errno);
        Assert.True(dir.HasInlineStats);

        byte[] targetUtf8 = Encoding.UTF8.GetBytes(entryName);
        while (dir.MoveNext())
        {
            if (dir.CurrentName.SequenceEqual(targetUtf8))
            {
                Assert.True(dir.TryGetInlineStat(out NativeStat stat));
                return stat;
            }
        }

        Assert.Fail($"Entry '{entryName}' not found in '{dirPath}'.");
        return default;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDu(string[] args, string programName)
    {
        using MemoryStream stdin = new([]);
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        int rc = DuCommand.Run(args, programName, "test", new DuCommand.StdStreams(stdin, stdout, stderr));
        return (rc, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSequentialDu(string[] args, string programName)
    {
        using MemoryStream stdin = new([]);
        using MemoryStream stdout = new();
        using MemoryStream stderr = new();
        DuParseOutcome outcome = DuOptions.Parse(args, programName, "test", stdin, stdout, stderr, out DuOptions? options);
        Assert.Equal(DuParseOutcome.Run, outcome);
        Assert.NotNull(options);
        int rc = new SequentialWalker(options, programName, stdin, stdout, stderr).Run();
        return (rc, Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    private static void CreateSparseFile(string path, long logicalSize, bool writeHeadAndTailChunks)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.True(
            DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);
        fs.SetLength(logicalSize);
        if (writeHeadAndTailChunks)
        {
            byte[] block = new byte[4096];
            block.AsSpan().Fill(0x5A);
            fs.Position = 0;
            fs.Write(block);
            fs.Position = logicalSize - block.Length;
            fs.Write(block);
        }

        fs.Flush(flushToDisk: true);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(Path.GetFullPath(linkPath));
        psi.ArgumentList.Add(Path.GetFullPath(targetPath));

        using Process? proc = Process.Start(psi);
        Assert.NotNull(proc);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(10_000), "Timed out running mklink /J.");
        Assert.True(proc.ExitCode == 0, $"mklink /J failed ({proc.ExitCode}): {stdout} {stderr}");
    }

    private static string? TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return linkPath;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(
        string lpPathName,
        IntPtr lpSecurityAttributes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteFileW(string lpFileName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveDirectoryW(string lpPathName);
}
