using System.IO;

namespace hyperdu.Core.Scanning;

public readonly struct LightweightEntry
{
    public string Name { get; init; } // Allocated only for directories we want to queue
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public FileAttributes Attributes { get; init; }
}
