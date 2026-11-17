using hyperdu.Core.Du;

namespace hyperdu.Core.Scanning;

public class ScanOptions
{
    public int WorkerCount { get; set; } = Environment.ProcessorCount;
    public bool FollowSymlinks { get; set; } = false;
    public bool SkipHidden { get; set; } = false;
    public bool ApparentSize { get; set; } = false;
    public bool CountLinks { get; set; } = false;
    public bool OneFileSystem { get; set; } = false;
    public List<string> ExcludedPaths { get; set; } = new();
    public ExcludeMatcher ExcludeMatcher { get; } = new();
    public ThresholdSpec? Threshold { get; set; }
    public BlockSizeSpec DisplayBlockSize { get; set; } = BlockSizeSpec.HumanBinary;

    /// <summary>
    /// Use direct libc calls on Linux and macOS. Falls back to the managed enumerator when unsupported.
    /// </summary>
    public bool UseNativeIo { get; set; } = true;

    internal bool AutoDetectWorkerCount { get; set; }
    internal bool Headless { get; set; }
}