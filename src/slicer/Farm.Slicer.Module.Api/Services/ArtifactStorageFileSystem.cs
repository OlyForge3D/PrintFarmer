using System.Globalization;

namespace Farm.Slicer.Module.Api.Services;

internal static class ArtifactStorageFileSystem
{
    internal const string StagingDirectoryName = ".staging";
    internal const string StagingFileExtension = ".upload";
    internal const string LeaseFileExtension = ".lease";

    internal static string ResolveRootPath(string configuredRoot, string contentRootPath)
    {
        string root = Path.IsPathFullyQualified(configuredRoot)
            ? configuredRoot
            : Path.Combine(contentRootPath, configuredRoot);
        return Path.GetFullPath(root);
    }

    internal static string GetStagingDirectory(string rootPath) =>
        Path.Combine(rootPath, StagingDirectoryName);

    internal static bool TryGetProtocolArtifactId(string path, out Guid artifactId)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.Length > 37 && fileName[36] == '-')
        {
            return Guid.TryParseExact(
                fileName.AsSpan(0, 36),
                "D",
                out artifactId);
        }

        artifactId = Guid.Empty;
        return false;
    }

    internal static bool TryGetStagingArtifactId(string path, out Guid artifactId)
    {
        string extension = Path.GetExtension(path);
        if (!string.Equals(extension, StagingFileExtension, StringComparison.Ordinal) &&
            !string.Equals(extension, LeaseFileExtension, StringComparison.Ordinal))
        {
            artifactId = Guid.Empty;
            return false;
        }

        return Guid.TryParseExact(
            Path.GetFileNameWithoutExtension(path),
            "N",
            out artifactId);
    }

    internal static string GetRelativePath(string rootPath, string fullPath) =>
        Path.GetRelativePath(rootPath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');

    internal static bool TryResolveArtifactPath(
        string rootPath,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathFullyQualified(relativePath))
        {
            return false;
        }

        string normalizedRoot;
        string candidate;
        try
        {
            normalizedRoot = Path.GetFullPath(rootPath);
            candidate = Path.GetFullPath(
                Path.Combine(
                    normalizedRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }

        if (!IsWithinRoot(normalizedRoot, candidate) ||
            ContainsReparsePoint(normalizedRoot, candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    internal static IEnumerable<string> EnumerateRegularFiles(string rootPath)
    {
        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        if (!root.Exists || IsReparsePoint(root))
        {
            yield break;
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out DirectoryInfo? directory))
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using IEnumerator<FileSystemInfo> enumerator = entries.GetEnumerator();
            while (true)
            {
                FileSystemInfo entry;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }

                    entry = enumerator.Current;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                if (IsReparsePoint(entry))
                {
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
                else if (entry is FileInfo file)
                {
                    yield return file.FullName;
                }
            }
        }
    }

    internal static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath));
        string normalizedCandidate = Path.GetFullPath(candidatePath);
        if (string.Equals(
                normalizedRoot,
                normalizedCandidate,
                pathComparison))
        {
            return false;
        }

        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(
            rootPrefix,
            pathComparison);
    }

    internal static bool IsReparsePoint(FileSystemInfo entry)
    {
        try
        {
            return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool ContainsReparsePoint(string rootPath, string candidatePath)
    {
        var root = new DirectoryInfo(Path.GetFullPath(rootPath));
        if (root.Exists && IsReparsePoint(root))
        {
            return true;
        }

        string? current = Path.GetDirectoryName(candidatePath);
        while (current is not null && IsWithinRoot(rootPath, current))
        {
            var directory = new DirectoryInfo(current);
            if (directory.Exists && IsReparsePoint(directory))
            {
                return true;
            }

            current = directory.Parent?.FullName;
        }

        var file = new FileInfo(candidatePath);
        return file.Exists && IsReparsePoint(file);
    }
}

internal sealed class ArtifactWriteLease : IDisposable
{
    private readonly FileStream _leaseStream;
    private bool _committed;
    private bool _disposed;

    private ArtifactWriteLease(
        Guid artifactId,
        string stagingPath,
        string leasePath,
        FileStream leaseStream)
    {
        ArtifactId = artifactId;
        StagingPath = stagingPath;
        LeasePath = leasePath;
        _leaseStream = leaseStream;
    }

    internal Guid ArtifactId { get; }

    internal string StagingPath { get; }

    internal string LeasePath { get; }

    internal string? PublishedPath { get; private set; }

    internal static ArtifactWriteLease Create(string rootPath, Guid artifactId)
    {
        string stagingDirectory =
            ArtifactStorageFileSystem.GetStagingDirectory(rootPath);
        _ = Directory.CreateDirectory(stagingDirectory);
        string identity = artifactId.ToString("N", CultureInfo.InvariantCulture);
        string stagingPath = Path.Combine(
            stagingDirectory,
            identity + ArtifactStorageFileSystem.StagingFileExtension);
        string leasePath = Path.Combine(
            stagingDirectory,
            identity + ArtifactStorageFileSystem.LeaseFileExtension);
        var leaseStream = new FileStream(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        return new ArtifactWriteLease(
            artifactId,
            stagingPath,
            leasePath,
            leaseStream);
    }

    internal FileStream OpenStagingStream() => new(
        StagingPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    internal void Publish(string fullPath, DateTime publishedAtUtc)
    {
        // The rename is same-volume and atomic. The locked lease remains until metadata commits,
        // making the otherwise unavoidable filesystem/database crash window distinguishable.
        File.Move(StagingPath, fullPath);
        PublishedPath = fullPath;
        File.SetLastWriteTimeUtc(fullPath, publishedAtUtc);
        File.SetLastWriteTimeUtc(LeasePath, publishedAtUtc);
    }

    internal void Commit()
    {
        _committed = true;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _leaseStream.Dispose();
        if (!_committed)
        {
            TryDelete(StagingPath);
            if (PublishedPath is not null)
            {
                TryDelete(PublishedPath);
            }
        }

        TryDelete(LeasePath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }
}
