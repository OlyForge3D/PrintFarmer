using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Farm.Web.Api.Services.IO;

namespace Farm.Web.Api.Tests.Services
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
            _files.TryRemove(path, out _);
        }

        public Stream OpenWrite(string path)
        {
            return new TestFileStream(this, path);
        }

        public Stream OpenRead(string path)
        {
            if (_files.TryGetValue(path, out var data))
            {
                return new MemoryStream(data);
            }
            if (File.Exists(path))
            {
                return File.OpenRead(path);
            }
            throw new FileNotFoundException(path);
        }

        public System.Threading.Tasks.Task<byte[]> ReadAllBytesAsync(string path, System.Threading.CancellationToken ct = default)
        {
            if (_files.TryGetValue(path, out var data))
            {
                return System.Threading.Tasks.Task.FromResult(data);
            }
            return System.Threading.Tasks.Task.FromResult(File.ReadAllBytes(path));
        }

        public System.Threading.Tasks.Task WriteAllBytesAsync(string path, byte[] data, System.Threading.CancellationToken ct = default)
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
            if (_files.TryGetValue(path, out var data))
            {
                return Encoding.UTF8.GetString(data);
            }
            return File.ReadAllText(path);
        }

        public string[] GetFiles(string path, string searchPattern, SearchOption option)
        {
            var prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var matches = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches;
        }

        public string[] GetDirectories(string path, string searchPattern, SearchOption option)
        {
            var prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var dirs = _dirs.Keys.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            return dirs;
        }

        public bool DirectoryIsEmpty(string path)
        {
            var prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return !_files.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !_dirs.Keys.Any(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<string> EnumerateFileSystemEntries(string path) => _files.Keys.Concat(_dirs.Keys);

        public FileInfoData GetFileInfo(string path)
        {
            if (_files.TryGetValue(path, out var data))
            {
                var times = _times.GetOrAdd(path, _ => (DateTime.UtcNow, DateTime.UtcNow));
                return new FileInfoData { Length = data.Length, CreationTimeUtc = times.Creation, LastWriteTimeUtc = times.LastWrite, Extension = Path.GetExtension(path) };
            }
            var fi = new System.IO.FileInfo(path);
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
            _times.AddOrUpdate(path, _ => (when, when), (k, v) => (when, v.LastWrite));
        }

        internal void SetLastWriteTimeUtc(string path, DateTime when)
        {
            _times.AddOrUpdate(path, _ => (when, when), (k, v) => (v.Creation, when));
        }

        public void DeleteDirectory(string path)
        {
            // Remove any files under this directory in the in-memory store
            var prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var keys = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var k in keys)
            {
                _files.TryRemove(k, out _);
            }
            var dirs = _dirs.Keys.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var d in dirs)
            {
                _dirs.TryRemove(d, out _);
            }
        }

        private class TestFileStream : MemoryStream
        {
            private readonly TestFileSystem _fs;
            private readonly string _path;

            public TestFileStream(TestFileSystem fs, string path)
            {
                _fs = fs;
                _path = path;
            }

            public override void Close()
            {
                Flush();
                _fs.Commit(_path, ToArray());
                base.Close();
            }
        }
    }
}
