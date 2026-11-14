namespace hyperdu.Core.Scanning;

public class ScanOptions
{
    public int WorkerCount { get; set; } = Environment.ProcessorCount;
    public bool FollowSymlinks { get; set; } = false;
    public bool SkipHidden { get; set; } = false;
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>
    /// Use direct libc calls on Linux and macOS. Falls back to the managed enumerator when unsupported.
    /// </summary>
    public bool UseNativeIo { get; set; } = true;
}