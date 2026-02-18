using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Farm.Infrastructure.IO;

namespace Farm.Slicer.Module.Tests.Services
{
    /// <summary>
    /// Simple in-memory file system for tests. Stores file contents in a concurrent dictionary keyed by full path.
    /// Only implements the operations used by tests: DirectoryExists, CreateDirectory, FileExists, DeleteFile, OpenWrite, GetFullPath, GetDirectoryName, GetFileName.
    /// </summary>
    public class TestFileSystem : IFileSystem
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new();
        private readonly ConcurrentDictionary<string, bool> _dirs = new();
        private readonly ConcurrentDictionary<string, (DateTime Creation, DateTime LastWrite)> _times = new();

        public bool DirectoryExists(string path) => _dirs.ContainsKey(path) || Directory.Exists(path);

        public void CreateDirectory(string path)
        {
            _dirs[path] = true;
        }

        public bool FileExists(string path) => _files.ContainsKey(path) || File.Exists(path);

        public void DeleteFile(string path)
        {
            _ = _files.TryRemove(path, out _);
        }

        public void MoveFile(string sourceFileName, string destFileName, bool overwrite = false)
        {
            if (_files.TryRemove(sourceFileName, out byte[]? data))
            {
                if (!overwrite && _files.ContainsKey(destFileName))
                {
                    throw new IOException($"File already exists: {destFileName}");
                }
                _files[destFileName] = data;

                // Move timestamps if they exist
                if (_times.TryRemove(sourceFileName, out (DateTime Creation, DateTime LastWrite) times))
                {
                    _times[destFileName] = times;
                }
            }
            else if (File.Exists(sourceFileName))
            {
                File.Move(sourceFileName, destFileName, overwrite);
            }
            else
            {
                throw new FileNotFoundException($"File not found: {sourceFileName}");
            }
        }

        public Stream OpenWrite(string path)
        {
            return new TestFileStream(this, path);
        }

        public Stream OpenRead(string path)
        {
            if (_files.TryGetValue(path, out byte[]? data))
            {
                return new MemoryStream(data);
            }
            return File.Exists(path) ? File.OpenRead(path) : throw new FileNotFoundException(path);
        }

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default)
        {
            return _files.TryGetValue(path, out byte[]? data)
                ? System.Threading.Tasks.Task.FromResult(data)
                : System.Threading.Tasks.Task.FromResult(File.ReadAllBytes(path));
        }

        public Task WriteAllBytesAsync(string path, byte[] data, CancellationToken ct = default)
        {
            Commit(path, data);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void WriteAllText(string path, string content)
        {
            Commit(path, Encoding.UTF8.GetBytes(content));
        }

        public string ReadAllText(string path)
        {
            return _files.TryGetValue(path, out byte[]? data) ? Encoding.UTF8.GetString(data) : File.ReadAllText(path);
        }

        public string[] GetFiles(string path, string searchPattern, SearchOption option)
        {
            string prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string[] matches = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches;
        }

        public string[] GetDirectories(string path, string searchPattern, SearchOption option)
        {
            string prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string[] dirs = _dirs.Keys.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return dirs;
        }

        public bool DirectoryIsEmpty(string path)
        {
            string prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return !_files.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !_dirs.Keys.Any(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<string> EnumerateFileSystemEntries(string path) => _files.Keys.Concat(_dirs.Keys);

        public FileInfoData GetFileInfo(string path)
        {
            if (_files.TryGetValue(path, out byte[]? data))
            {
                (DateTime Creation, DateTime LastWrite) times = _times.GetOrAdd(path, _ => (DateTime.UtcNow, DateTime.UtcNow));
                return new FileInfoData { Length = data.Length, CreationTimeUtc = times.Creation, LastWriteTimeUtc = times.LastWrite, Extension = Path.GetExtension(path) };
            }
            FileInfo fi = new FileInfo(path);
            return new FileInfoData { Length = fi.Length, CreationTimeUtc = fi.CreationTimeUtc, LastWriteTimeUtc = fi.LastWriteTimeUtc, Extension = fi.Extension };
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);

        public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? string.Empty;

        public string GetFileName(string path) => Path.GetFileName(path);

        internal void Commit(string path, byte[] data)
        {
            _files[path] = data;
            _times[path] = (DateTime.UtcNow, DateTime.UtcNow);
        }

        internal void SetCreationTimeUtc(string path, DateTime when)
        {
            _ = _times.AddOrUpdate(path, _ => (when, when), (k, v) => (when, v.LastWrite));
        }

        internal void SetLastWriteTimeUtc(string path, DateTime when)
        {
            _ = _times.AddOrUpdate(path, _ => (when, when), (k, v) => (v.Creation, when));
        }

        public void DeleteDirectory(string path)
        {
            // Remove any files under this directory in the in-memory store
            string prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string[] keys = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (string? k in keys)
            {
                _ = _files.TryRemove(k, out _);
            }
            string[] dirs = _dirs.Keys.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (string? d in dirs)
            {
                _ = _dirs.TryRemove(d, out _);
            }
        }

        private class TestFileStream(TestFileSystem fs, string path) : MemoryStream
        {
            private readonly TestFileSystem _fs = fs;
            private readonly string _path = path;

            public override void Close()
            {
                Flush();
                _fs.Commit(_path, ToArray());
                base.Close();
            }
        }
    }
}
