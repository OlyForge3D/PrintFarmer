using System.IO;

namespace Farm.Web.Api.Services.IO
{
    public class SystemFileSystem : IFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public bool FileExists(string path) => File.Exists(path);
        public void DeleteFile(string path) => File.Delete(path);
        public void MoveFile(string sourceFileName, string destFileName, bool overwrite = false) => File.Move(sourceFileName, destFileName, overwrite);
        public Stream OpenWrite(string path) => new FileStream(path, FileMode.Create, FileAccess.Write);
        public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read);
        public async System.Threading.Tasks.Task<byte[]> ReadAllBytesAsync(string path, System.Threading.CancellationToken ct = default)
            => await File.ReadAllBytesAsync(path, ct);
        public async System.Threading.Tasks.Task WriteAllBytesAsync(string path, byte[] data, System.Threading.CancellationToken ct = default)
            => await File.WriteAllBytesAsync(path, data, ct);
        public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
        public string ReadAllText(string path) => File.ReadAllText(path);
        public string[] GetFiles(string path, string searchPattern, System.IO.SearchOption option) => Directory.GetFiles(path, searchPattern, option);
        public string[] GetDirectories(string path, string searchPattern, System.IO.SearchOption option) => Directory.GetDirectories(path, searchPattern, option);
        public bool DirectoryIsEmpty(string path) => !Directory.EnumerateFileSystemEntries(path).Any();
        public System.Collections.Generic.IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
        public FileInfoData GetFileInfo(string path)
        {
            System.IO.FileInfo fi = new System.IO.FileInfo(path);
            return new FileInfoData
            {
                Length = fi.Length,
                CreationTimeUtc = fi.CreationTimeUtc,
                LastWriteTimeUtc = fi.LastWriteTimeUtc,
                Extension = fi.Extension
            };
        }
        public void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        public string GetFullPath(string path) => Path.GetFullPath(path);
        public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? string.Empty;
        public string GetFileName(string path) => Path.GetFileName(path);
    }
}
