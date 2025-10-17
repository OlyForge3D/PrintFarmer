using System.IO;

namespace Farm.Web.Api.Services.IO
{
    public interface IFileSystem
    {
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        bool FileExists(string path);
        void DeleteFile(string path);
        Stream OpenWrite(string path);
        Stream OpenRead(string path);
        System.Threading.Tasks.Task<byte[]> ReadAllBytesAsync(string path, System.Threading.CancellationToken ct = default);
        System.Threading.Tasks.Task WriteAllBytesAsync(string path, byte[] data, System.Threading.CancellationToken ct = default);
        void WriteAllText(string path, string content);
        string ReadAllText(string path);
        string[] GetFiles(string path, string searchPattern, System.IO.SearchOption option);
        string[] GetDirectories(string path, string searchPattern, System.IO.SearchOption option);
        bool DirectoryIsEmpty(string path);
        System.Collections.Generic.IEnumerable<string> EnumerateFileSystemEntries(string path);
        FileInfoData GetFileInfo(string path);
        void DeleteDirectory(string path);
        string GetFullPath(string path);
        string GetDirectoryName(string path);
        string GetFileName(string path);
    }

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
}
