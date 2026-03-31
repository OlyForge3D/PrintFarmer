using System.IO;

namespace Farm.Infrastructure.IO;

/// <summary>
/// Abstraction for file system operations to enable testability and platform independence.
/// </summary>
public interface IFileSystem
{
    /// <summary>Checks if a directory exists at the specified path.</summary>
    bool DirectoryExists(string path);

    /// <summary>Creates a directory at the specified path.</summary>
    void CreateDirectory(string path);

    /// <summary>Checks if a file exists at the specified path.</summary>
    bool FileExists(string path);

    /// <summary>Deletes a file at the specified path.</summary>
    void DeleteFile(string path);

    /// <summary>Moves a file from source to destination.</summary>
    void MoveFile(string sourceFileName, string destFileName, bool overwrite = false);

    /// <summary>Opens a file for writing, creating it if it doesn't exist.</summary>
    Stream OpenWrite(string path);

    /// <summary>Opens a file for reading.</summary>
    Stream OpenRead(string path);

    /// <summary>Reads all bytes from a file asynchronously.</summary>
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default);

    /// <summary>Writes all bytes to a file asynchronously.</summary>
    Task WriteAllBytesAsync(string path, byte[] data, CancellationToken ct = default);

    /// <summary>Writes text content to a file.</summary>
    void WriteAllText(string path, string content);

    /// <summary>Reads all text from a file.</summary>
    string ReadAllText(string path);

    /// <summary>Gets files matching a search pattern.</summary>
    string[] GetFiles(string path, string searchPattern, SearchOption option);

    /// <summary>Gets directories matching a search pattern.</summary>
    string[] GetDirectories(string path, string searchPattern, SearchOption option);

    /// <summary>Checks if a directory is empty.</summary>
    bool DirectoryIsEmpty(string path);

    /// <summary>Enumerates all file system entries in a directory.</summary>
    IEnumerable<string> EnumerateFileSystemEntries(string path);

    /// <summary>Gets file information for a file.</summary>
    FileInfoData GetFileInfo(string path);

    /// <summary>Deletes a directory and its contents.</summary>
    void DeleteDirectory(string path);

    /// <summary>Gets the full absolute path for a relative path.</summary>
    string GetFullPath(string path);

    /// <summary>Gets the directory name from a path.</summary>
    string GetDirectoryName(string path);

    /// <summary>Gets the file name from a path.</summary>
    string GetFileName(string path);
}

/// <summary>
/// File information data structure.
/// </summary>
public struct FileInfoData : IEquatable<FileInfoData>
{
    public long Length { get; set; }

    public DateTime CreationTimeUtc { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }

    public string Extension { get; set; }

    public override bool Equals(object? obj) => obj is FileInfoData other && Equals(other);

    public bool Equals(FileInfoData other) => Length == other.Length && CreationTimeUtc == other.CreationTimeUtc && LastWriteTimeUtc == other.LastWriteTimeUtc && string.Equals(Extension, other.Extension, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Length, CreationTimeUtc, LastWriteTimeUtc, Extension);

    public static bool operator ==(FileInfoData left, FileInfoData right) => left.Equals(right);

    public static bool operator !=(FileInfoData left, FileInfoData right) => !left.Equals(right);
}
