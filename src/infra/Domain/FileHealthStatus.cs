namespace Farm.Infrastructure.Domain;

public enum FileHealthStatus
{
    Unknown = 0,      // Never checked or status unknown
    Healthy = 1,      // File exists, hash and size match
    Missing = 2,      // File not found on disk
    Corrupted = 3,    // File exists but hash/size mismatch
    Inaccessible = 4  // File exists but cannot be read (permission denied)
}
