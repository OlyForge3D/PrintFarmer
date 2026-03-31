using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Small helper to create pre-seeded TestFileSystem instances for common test scenarios.
/// </summary>
public static class TestFileSystemFactory
{
    /// <summary>
    /// Creates a TestFileSystem with a single file at the provided path containing the given content.
    /// Ensures the directory exists and the file is committed.
    /// </summary>
    public static TestFileSystem WithFile(string filePath, byte[]? content = null)
    {
        TestFileSystem fs = new TestFileSystem();
        string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        if (!string.IsNullOrEmpty(dir))
        {
            fs.CreateDirectory(dir);
        }
        fs.Commit(filePath, content ?? Encoding.UTF8.GetBytes("content"));
        return fs;
    }

    /// <summary>
    /// Creates a TestFileSystem with a thumbnail file (png) at the given path.
    /// </summary>
    public static TestFileSystem WithThumbnail(string thumbPath, byte[]? content = null)
        => WithFile(thumbPath, content ?? Encoding.UTF8.GetBytes("pngcontent"));

    /// <summary>
    /// Creates a TestFileSystem seeded with multiple files.
    /// </summary>
    public static TestFileSystem WithFiles(IDictionary<string, byte[]> files)
    {
        TestFileSystem fs = new TestFileSystem();
        foreach (KeyValuePair<string, byte[]> kv in files)
        {
            string dir = Path.GetDirectoryName(kv.Key) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir))
            {
                fs.CreateDirectory(dir);
            }
            fs.Commit(kv.Key, kv.Value);
        }
        return fs;
    }
}
