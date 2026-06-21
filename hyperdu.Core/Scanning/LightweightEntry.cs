using System.IO;

namespace hyperdu.Core.Scanning;

public struct LightweightEntry
{
    public string Name; // Allocated only for directories we want to queue
    public bool IsDirectory;
    public long Length;
    public FileAttributes Attributes;
}
