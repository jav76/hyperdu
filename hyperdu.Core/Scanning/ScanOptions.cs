using System;
using System.Collections.Generic;

namespace hyperdu.Core.Scanning;

public class ScanOptions
{
    public int WorkerCount { get; set; } = Environment.ProcessorCount;
    public bool FollowSymlinks { get; set; } = false;
    public bool SkipHidden { get; set; } = false;
    public List<string> ExcludedPaths { get; set; } = new();
}
