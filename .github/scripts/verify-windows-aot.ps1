param(
    [Parameter(Mandatory = $true)]
    [string]$BinDir,

    [string]$MarkdownPath = ""
)

$ErrorActionPreference = 'Stop'

$hyperdu = Join-Path $BinDir 'hyperdu.exe'
$du = Join-Path $BinDir 'du.exe'
$gdu = Join-Path $BinDir 'gdu.exe'

if (-not (Test-Path $hyperdu)) { throw "Missing $hyperdu" }
if (-not (Test-Path $du)) { Copy-Item -Path $hyperdu -Destination $du -Force }
if (-not (Test-Path $gdu)) { Copy-Item -Path $hyperdu -Destination $gdu -Force }

Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class WinCompatHelper
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE = 0x00000003;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string lpPathName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string lpPathName);

    private static string Ext(string path)
    {
        string full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    }

    public static void CreateSparseFile(string path, long logicalBytes)
    {
        using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (!DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        fs.SetLength(logicalBytes);
        byte[] block = new byte[4096];
        for (int i = 0; i < block.Length; i++) block[i] = 0x5A;
        fs.Position = 0;
        fs.Write(block, 0, block.Length);
        fs.Position = logicalBytes - block.Length;
        fs.Write(block, 0, block.Length);
        fs.Flush(true);
    }

    public static void CreateLongDirectory(string path)
    {
        if (!CreateDirectoryW(Ext(path), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void WriteLongFile(string path, int byteLength)
    {
        using SafeFileHandle h = CreateFileW(Ext(path), GENERIC_WRITE, FILE_SHARE_READ_WRITE, IntPtr.Zero, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (h.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using FileStream fs = new FileStream(h, FileAccess.Write);
        byte[] data = new byte[byteLength];
        for (int i = 0; i < data.Length; i++) data[i] = 0x42;
        fs.Write(data, 0, data.Length);
        fs.Flush(true);
    }

    public static void DeleteLongFile(string path) { _ = DeleteFileW(Ext(path)); }
    public static void RemoveLongDirectory(string path) { _ = RemoveDirectoryW(Ext(path)); }
}
"@

function Invoke-Checked {
    param(
        [string]$Exe,
        [string[]]$ArgList,
        [switch]$HeadlessTui
    )
    $oldUi = $env:HYPERDU_NO_UI
    try {
        if ($HeadlessTui) { $env:HYPERDU_NO_UI = '1' } else { Remove-Item Env:HYPERDU_NO_UI -ErrorAction SilentlyContinue }
        $out = & $Exe @ArgList
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed ($LASTEXITCODE): $Exe $($ArgList -join ' ')"
        }
        return ($out -join "`n").Trim()
    }
    finally {
        if ($null -ne $oldUi) { $env:HYPERDU_NO_UI = $oldUi } else { Remove-Item Env:HYPERDU_NO_UI -ErrorAction SilentlyContinue }
    }
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("hyperdu-win-aot-" + [Guid]::NewGuid().ToString('N'))
$deepDirs = New-Object System.Collections.Generic.List[string]
$deepFile = $null
New-Item -ItemType Directory -Path $root -Force | Out-Null

try {
    # 1. Version & persona dispatch
    $vHyperdu = Invoke-Checked $hyperdu @('du', '--version')
    $vDu = Invoke-Checked $du @('--version')
    $vGdu = Invoke-Checked $gdu @('--version')
    if (-not $vDu.StartsWith('du ')) { throw "Unexpected du.exe --version: $vDu" }
    if (-not $vGdu.StartsWith('gdu ')) { throw "Unexpected gdu.exe --version: $vGdu" }

    # 2. Sparse file (NtQueryDirectoryFile AllocationSize vs EndOfFile)
    $sparseDir = Join-Path $root 'sparse'
    New-Item -ItemType Directory -Path $sparseDir -Force | Out-Null
    [WinCompatHelper]::CreateSparseFile((Join-Path $sparseDir 'sparse.bin'), 4194304)
    $sparseApparent = [long]((Invoke-Checked $du @('-b', '-s', $sparseDir)) -split "`t")[0]
    $sparseAlloc = [long]((Invoke-Checked $du @('-B1', '-s', $sparseDir)) -split "`t")[0]
    $headlessSparseAlloc = [long]((Invoke-Checked $hyperdu @($sparseDir) -HeadlessTui) -split "`t")[0]
    if ($sparseApparent -ne 4194304) { throw "Expected 4194304 sparse apparent bytes, got $sparseApparent" }
    if ($sparseAlloc -le 0 -or $sparseAlloc -ge 524288) { throw "Expected sparse allocated < 524288, got $sparseAlloc" }
    if ($headlessSparseAlloc -ne $sparseAlloc) { throw "Headless TUI mismatch: $headlessSparseAlloc vs du $sparseAlloc" }

    # 3. Hard link dedupe & -l
    $hlDir = Join-Path $root 'hardlinks'
    New-Item -ItemType Directory -Path $hlDir -Force | Out-Null
    $origFile = Join-Path $hlDir 'orig.bin'
    $linkFile = Join-Path $hlDir 'link.bin'
    [WinCompatHelper]::WriteLongFile($origFile, 32768)
    & cmd.exe /d /c mklink /H "$linkFile" "$origFile" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "mklink /H failed" }
    $hlDedupe = [long]((Invoke-Checked $du @('-b', '-s', $hlDir)) -split "`t")[0]
    $hlAll = [long]((Invoke-Checked $du @('-b', '-s', '-l', $hlDir)) -split "`t")[0]
    if ($hlDedupe -ne 32768) { throw "Expected 32768 hardlink dedupe bytes, got $hlDedupe" }
    if ($hlAll -ne 65536) { throw "Expected 65536 -l hardlink bytes, got $hlAll" }

    # 4. Junctions (-P, -H, -L, cycle, and separate broken junction)
    $juncRoot = Join-Path $root 'junctions'
    $juncTarget = Join-Path $juncRoot 'target'
    $juncParent = Join-Path $juncRoot 'links'
    $brokenParent = Join-Path $juncRoot 'broken_holder'
    New-Item -ItemType Directory -Path $juncTarget, $juncParent, $brokenParent -Force | Out-Null
    [WinCompatHelper]::WriteLongFile((Join-Path $juncTarget 'data.bin'), 16384)
    $juncLink = Join-Path $juncParent 'j'
    & cmd.exe /d /c mklink /J "$juncLink" "$juncTarget" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "mklink /J failed" }

    $ephemeral = Join-Path $juncRoot 'ephemeral'
    New-Item -ItemType Directory -Path $ephemeral -Force | Out-Null
    $brokenLink = Join-Path $brokenParent 'broken'
    & cmd.exe /d /c mklink /J "$brokenLink" "$ephemeral" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "mklink /J broken failed" }
    Remove-Item -Path $ephemeral -Force

    & cmd.exe /d /c mklink /J (Join-Path $juncParent 'cycle') "$juncParent" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "mklink /J cycle failed" }

    $juncP = [long]((Invoke-Checked $du @('-P', '-b', '-s', $juncParent)) -split "`t")[0]
    $juncH = [long]((Invoke-Checked $du @('-H', '-b', '-s', $juncLink)) -split "`t")[0]
    $juncL = [long]((Invoke-Checked $du @('-L', '-b', '-s', $juncParent)) -split "`t")[0]
    if ($juncP -ne 0) { throw "Expected 0 bytes for -P junctions, got $juncP" }
    if ($juncH -ne 16384) { throw "Expected 16384 bytes for -H junction arg, got $juncH" }
    if ($juncL -ne 16384) { throw "Expected 16384 bytes for -L junction tree, got $juncL" }

    $brokenOut = & $du -L -b -s "$brokenParent" 2>$null
    if ($LASTEXITCODE -ne 1) { throw "Expected exit code 1 for broken junction under -L, got $LASTEXITCODE" }
    if (-not $brokenOut) { throw "Expected stdout line for broken junction container" }

    # 5. Deep > MAX_PATH tree
    $deepRoot = Join-Path $root 'deep'
    [WinCompatHelper]::CreateLongDirectory($deepRoot)
    $deepDirs.Add($deepRoot)
    $cursor = $deepRoot
    for ($i = 0; $i -lt 35; $i++) {
        $cursor = Join-Path $cursor ("seg_{0:D2}_pad0123456789" -f $i)
        [WinCompatHelper]::CreateLongDirectory($cursor)
        $deepDirs.Add($cursor)
    }
    $deepFile = Join-Path $cursor 'leaf.bin'
    [WinCompatHelper]::WriteLongFile($deepFile, 8192)
    $deepBytes = [long]((Invoke-Checked $du @('-b', '-s', $deepRoot)) -split "`t")[0]
    $deepTuiBytes = [long]((Invoke-Checked $hyperdu @('-b', $deepRoot) -HeadlessTui) -split "`t")[0]
    if ($deepBytes -ne 8192 -or $deepTuiBytes -ne 8192) {
        throw "Deep >MAX_PATH mismatch: du=$deepBytes tui=$deepTuiBytes"
    }

    $firstVer = ($vDu -split "`n")[0]
    $deepLen = $cursor.Length
    $summary = @(
        "### Windows NativeAOT Check ($BinDir)"
        ""
        "| Check | Expected | Observed |"
        "| --- | --- | --- |"
        "| Persona version (du.exe) | du ... | $firstVer |"
        "| Sparse allocation vs logical | < 524288 / 4194304 | $sparseAlloc / $sparseApparent |"
        "| Hard links default vs -l | 32768 / 65536 | $hlDedupe / $hlAll |"
        "| Junctions -P / -H / -L | 0 / 16384 / 16384 | $juncP / $juncH / $juncL |"
        "| Deep > MAX_PATH ($deepLen chars) | 8192 | $deepBytes (TUI $deepTuiBytes) |"
    ) -join "`n"

    Write-Output $summary
    if ($MarkdownPath) {
        $parentMd = Split-Path -Parent $MarkdownPath
        if ($parentMd) { New-Item -ItemType Directory -Path $parentMd -Force | Out-Null }
        Set-Content -Path $MarkdownPath -Value $summary -Encoding UTF8
    }
}
finally {
    if ($deepFile) { [WinCompatHelper]::DeleteLongFile($deepFile) }
    for ($i = $deepDirs.Count - 1; $i -ge 0; $i--) {
        [WinCompatHelper]::RemoveLongDirectory($deepDirs[$i])
    }
    if (Test-Path $root) {
        Get-ChildItem -Path $root -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            ForEach-Object { cmd.exe /d /c rmdir "$($_.FullName)" 2>$null | Out-Null }
        Remove-Item -Path $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}
