using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
        string fullRoot = Path.GetFullPath(root);
        var rootDirectory = new DirectoryInfo(fullRoot);
        if (rootDirectory.Exists && IsReparsePoint(rootDirectory))
        {
            try
            {
                FileSystemInfo? resolvedRoot =
                    rootDirectory.ResolveLinkTarget(returnFinalTarget: true);
                if (resolvedRoot is DirectoryInfo resolvedDirectory)
                {
                    return Path.GetFullPath(resolvedDirectory.FullName);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return fullRoot;
            }
        }

        return fullRoot;
    }

    internal static string GetStagingDirectory(string rootPath) =>
        Path.Combine(rootPath, StagingDirectoryName);

    internal static string EnsureArtifactRoot(string rootPath)
    {
        DirectoryInfo rootDirectory =
            Directory.CreateDirectory(Path.GetFullPath(rootPath));
        if (IsReparsePoint(rootDirectory))
        {
            throw new IOException(
                "The resolved artifact root must not be a reparse point.");
        }

        return rootDirectory.FullName;
    }

    internal static string EnsureStagingDirectory(string rootPath)
    {
        string normalizedRoot = EnsureArtifactRoot(rootPath);
        string stagingPath = Path.GetFullPath(
            GetStagingDirectory(normalizedRoot));
        if (!IsWithinRoot(normalizedRoot, stagingPath))
        {
            throw new IOException(
                "The artifact staging directory is outside the artifact root.");
        }

        DirectoryInfo stagingDirectory = Directory.CreateDirectory(stagingPath);
        if (IsReparsePoint(stagingDirectory))
        {
            throw new IOException(
                "The artifact staging directory must not be a reparse point.");
        }

        return stagingDirectory.FullName;
    }

    internal static string EnsureSafePublicationPath(
        string rootPath,
        string fullPath)
    {
        string normalizedRoot = Path.GetFullPath(rootPath);
        string normalizedPath = Path.GetFullPath(fullPath);
        if (!IsWithinRoot(normalizedRoot, normalizedPath) ||
            ContainsReparsePoint(normalizedRoot, normalizedPath))
        {
            throw new IOException(
                "The artifact publication path is not safely contained.");
        }

        return normalizedPath;
    }

    [SuppressMessage(
        "Reliability",
        "S3869:SafeHandle.DangerousGetHandle should not be called",
        Justification = "The handle is pinned with DangerousAddRef and released in finally.")]
    internal static void CreateAtomicHardLink(
        string publicationPath,
        string stagingPath,
        SafeFileHandle stagingHandle)
    {
        int error;
        if (OperatingSystem.IsWindows())
        {
            string publicationDirectory =
                Path.GetDirectoryName(publicationPath) ??
                throw new IOException(
                    "The artifact publication path has no parent directory.");
            using SafeFileHandle publicationDirectoryHandle =
                OpenPinnedWindowsDirectory(publicationDirectory);
            if (CreateHardLinkWindows(
                    publicationPath,
                    stagingPath,
                    IntPtr.Zero))
            {
                return;
            }

            error = Marshal.GetLastPInvokeError();
        }
        else if (OperatingSystem.IsLinux())
        {
            bool handleAdded = false;
            try
            {
                stagingHandle.DangerousAddRef(ref handleAdded);
                int fileDescriptor =
                    stagingHandle.DangerousGetHandle().ToInt32();
                string descriptorPath =
                    $"/proc/self/fd/{fileDescriptor}";
                string publicationDirectory =
                    Path.GetDirectoryName(publicationPath) ??
                    throw new IOException(
                        "The artifact publication path has no parent directory.");
                int publicationDirectoryFileDescriptor = OpenLinuxFile(
                    publicationDirectory,
                    LinuxDirectoryFlags);
                if (publicationDirectoryFileDescriptor < 0)
                {
                    error = Marshal.GetLastPInvokeError();
                }
                else
                {
                    try
                    {
                        if (CreateHardLinkUnixFromHandle(
                                LinuxCurrentWorkingDirectory,
                                descriptorPath,
                                publicationDirectoryFileDescriptor,
                                Path.GetFileName(publicationPath),
                                LinuxFollowSymbolicLink) == 0)
                        {
                            return;
                        }

                        error = Marshal.GetLastPInvokeError();
                    }
                    finally
                    {
                        _ = CloseLinuxFile(
                            publicationDirectoryFileDescriptor);
                    }
                }
            }
            finally
            {
                if (handleAdded)
                {
                    stagingHandle.DangerousRelease();
                }
            }
        }
        else
        {
            throw new PlatformNotSupportedException(
                "Atomic artifact publication is not supported on this platform.");
        }

        throw new IOException(
            "Failed to atomically publish the artifact on the same filesystem.",
            new Win32Exception(error));
    }

    private static SafeFileHandle OpenPinnedWindowsDirectory(string path)
    {
        SafeFileHandle handle = OpenWindowsFile(
            path,
            WindowsReadAttributes,
            WindowsShareRead | WindowsShareWrite,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsOpenReparsePoint | WindowsBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            ThrowWindowsDeletionException(path, error);
        }

        try
        {
            if (!GetWindowsFileInformation(
                    handle,
                    WindowsFileInfoByHandleClass.FileBasicInfo,
                    out WindowsFileBasicInfo fileInfo,
                    (uint)Marshal.SizeOf<WindowsFileBasicInfo>()))
            {
                ThrowWindowsDeletionException(
                    path,
                    Marshal.GetLastPInvokeError());
            }

            if ((fileInfo.FileAttributes & WindowsDirectoryAttribute) == 0 ||
                (fileInfo.FileAttributes & WindowsReparsePointAttribute) != 0)
            {
                throw new IOException(
                    $"The artifact publication parent '{path}' is not a safe directory.");
            }

            string expectedPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(path));
            string resolvedPath = Path.TrimEndingDirectorySeparator(
                GetWindowsFinalPath(handle));
            if (!string.Equals(
                    expectedPath,
                    resolvedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The artifact publication directory resolved to an unexpected path.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static FileStream CreateStagingStream(string stagingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            string windowsStagingDirectory = Path.GetDirectoryName(stagingPath) ??
                throw new IOException(
                    "The artifact staging path has no parent directory.");
            using SafeFileHandle stagingDirectoryHandle =
                OpenPinnedWindowsDirectory(windowsStagingDirectory);
            return new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Artifact staging is not supported on this platform.");
        }

        string stagingDirectory = Path.GetDirectoryName(stagingPath) ??
            throw new IOException(
                "The artifact staging path has no parent directory.");
        int fileDescriptor = OpenAnonymousLinuxFile(
            stagingDirectory,
            LinuxAnonymousFileFlags,
            LinuxOwnerReadWrite);
        if (fileDescriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is LinuxInvalidArgument or LinuxOperationNotSupported)
            {
                return CreateNamedLinuxStagingStream(stagingPath);
            }
            else
            {
                throw new IOException(
                    "Failed to create an anonymous artifact staging file.",
                    new Win32Exception(error));
            }
        }

        return CreateLinuxFileStream(fileDescriptor);
    }

    internal static FileStream CreateLeaseStream(string leasePath)
    {
        if (OperatingSystem.IsLinux())
        {
            return CreateNamedLinuxStagingStream(leasePath);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Artifact leases are not supported on this platform.");
        }

        string stagingDirectory = Path.GetDirectoryName(leasePath) ??
            throw new IOException(
                "The artifact lease path has no parent directory.");
        using SafeFileHandle stagingDirectoryHandle =
            OpenPinnedWindowsDirectory(stagingDirectory);
        return new FileStream(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
    }

    internal static FileStream CreateNamedLinuxStagingStream(
        string stagingPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Named Linux artifact staging is only available on Linux.");
        }

        string stagingDirectory = Path.GetDirectoryName(stagingPath) ??
            throw new IOException(
                "The artifact staging path has no parent directory.");
        int fileDescriptor = OpenNamedLinuxFile(
            stagingDirectory,
            Path.GetFileName(stagingPath));
        return CreateLinuxFileStream(fileDescriptor);
    }

    private static FileStream CreateLinuxFileStream(int fileDescriptor)
    {
        var handle = new SafeFileHandle(
            new IntPtr(fileDescriptor),
            ownsHandle: true);
        try
        {
            return new FileStream(
                handle,
                FileAccess.ReadWrite,
                bufferSize: 81920,
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static int OpenNamedLinuxFile(
        string stagingDirectory,
        string stagingFileName)
    {
        int directoryFileDescriptor = OpenLinuxFile(
            stagingDirectory,
            LinuxDirectoryFlags);
        if (directoryFileDescriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "Failed to open the artifact staging directory.",
                new Win32Exception(error));
        }

        try
        {
            int fileDescriptor = OpenLinuxFileAt(
                directoryFileDescriptor,
                stagingFileName,
                LinuxNamedFileFlags,
                LinuxOwnerReadWrite);
            if (fileDescriptor >= 0)
            {
                return fileDescriptor;
            }

            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "Failed to create a named artifact staging file.",
                new Win32Exception(error));
        }
        finally
        {
            _ = CloseLinuxFile(directoryFileDescriptor);
        }
    }

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

        string rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
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

    internal static void DeleteFileNoFollow(string rootPath, string fullPath)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath));
        string normalizedPath = Path.GetFullPath(fullPath);
        if (!IsWithinRoot(normalizedRoot, normalizedPath))
        {
            throw new IOException(
                "The artifact deletion path is outside the artifact root.");
        }

        string relativePath = Path.GetRelativePath(
            normalizedRoot,
            normalizedPath);
        string[] components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 ||
            components.Any(component => component is "." or ".."))
        {
            throw new IOException(
                "The artifact deletion path is not safely contained.");
        }

        if (OperatingSystem.IsWindows())
        {
            DeleteWindowsFileNoFollow(
                normalizedRoot,
                normalizedPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            DeleteLinuxFileNoFollow(
                normalizedRoot,
                components,
                normalizedPath);
            return;
        }

        throw new PlatformNotSupportedException(
            "Race-safe artifact deletion is not supported on this platform.");
    }

    private static void DeleteLinuxFileNoFollow(
        string rootPath,
        string[] components,
        string fullPath)
    {
        int directoryFileDescriptor = OpenLinuxFile(
            rootPath,
            LinuxDirectoryFlags);
        if (directoryFileDescriptor < 0)
        {
            ThrowLinuxDeletionException(
                fullPath,
                Marshal.GetLastPInvokeError());
            return;
        }

        try
        {
            for (int index = 0; index < components.Length - 1; index++)
            {
                int childFileDescriptor = OpenLinuxFileAt(
                    directoryFileDescriptor,
                    components[index],
                    LinuxDirectoryFlags,
                    mode: 0);
                if (childFileDescriptor < 0)
                {
                    ThrowLinuxDeletionException(
                        fullPath,
                        Marshal.GetLastPInvokeError());
                    return;
                }

                _ = CloseLinuxFile(directoryFileDescriptor);
                directoryFileDescriptor = childFileDescriptor;
            }

            if (DeleteLinuxFileAt(
                    directoryFileDescriptor,
                    components[^1],
                    flags: 0) != 0)
            {
                ThrowLinuxDeletionException(
                    fullPath,
                    Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            _ = CloseLinuxFile(directoryFileDescriptor);
        }
    }

    private static void ThrowLinuxDeletionException(
        string path,
        int error)
    {
        if (error is LinuxNoSuchFileOrDirectory)
        {
            return;
        }

        var innerException = new Win32Exception(error);
        if (error is LinuxAccessDenied or LinuxOperationNotPermitted)
        {
            throw new UnauthorizedAccessException(
                $"Access was denied while deleting artifact file '{path}'.",
                innerException);
        }

        throw new IOException(
            $"Failed to safely delete artifact file '{path}'.",
            innerException);
    }

    private static void DeleteWindowsFileNoFollow(
        string rootPath,
        string fullPath)
    {
        using SafeFileHandle handle = OpenWindowsFile(
            fullPath,
            WindowsDelete | WindowsReadAttributes,
            WindowsShareRead | WindowsShareWrite,
            IntPtr.Zero,
            WindowsOpenExisting,
            WindowsOpenReparsePoint | WindowsBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is WindowsFileNotFound or WindowsPathNotFound)
            {
                return;
            }

            ThrowWindowsDeletionException(fullPath, error);
        }

        WindowsFileBasicInfo fileInfo;
        if (!GetWindowsFileInformation(
                handle,
                WindowsFileInfoByHandleClass.FileBasicInfo,
                out fileInfo,
                (uint)Marshal.SizeOf<WindowsFileBasicInfo>()))
        {
            ThrowWindowsDeletionException(
                fullPath,
                Marshal.GetLastPInvokeError());
        }

        if ((fileInfo.FileAttributes &
                (WindowsDirectoryAttribute | WindowsReparsePointAttribute)) != 0)
        {
            throw new IOException(
                $"Refusing to delete directory or reparse entry '{fullPath}'.");
        }

        string resolvedPath = GetWindowsFinalPath(handle);
        if (!IsWithinRoot(rootPath, resolvedPath))
        {
            throw new IOException(
                "The artifact deletion handle resolved outside the artifact root.");
        }

        var disposition = new WindowsFileDispositionInfo
        {
            DeleteFile = true,
        };
        if (!SetWindowsFileInformation(
                handle,
                WindowsFileInfoByHandleClass.FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<WindowsFileDispositionInfo>()))
        {
            ThrowWindowsDeletionException(
                fullPath,
                Marshal.GetLastPInvokeError());
        }
    }

    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        uint requiredLength = GetWindowsFinalPathName(
            handle,
            path: null,
            capacity: 0,
            WindowsNormalizedDosPath);
        if (requiredLength == 0)
        {
            throw new IOException(
                "Failed to resolve the artifact deletion handle.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var path = new char[checked((int)requiredLength + 1)];
        uint written = GetWindowsFinalPathName(
            handle,
            path,
            path.Length,
            WindowsNormalizedDosPath);
        if (written == 0 || written >= path.Length)
        {
            throw new IOException(
                "Failed to resolve the artifact deletion handle.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        string resolvedPath = new(path, 0, (int)written);
        if (resolvedPath.StartsWith(
                WindowsExtendedUncPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + resolvedPath[WindowsExtendedUncPrefix.Length..];
        }

        return resolvedPath.StartsWith(
            WindowsExtendedPathPrefix,
            StringComparison.OrdinalIgnoreCase)
            ? resolvedPath[WindowsExtendedPathPrefix.Length..]
            : resolvedPath;
    }

    private static void ThrowWindowsDeletionException(
        string path,
        int error)
    {
        var innerException = new Win32Exception(error);
        if (error is WindowsAccessDenied or WindowsSharingViolation)
        {
            throw new UnauthorizedAccessException(
                $"Access was denied while deleting artifact file '{path}'.",
                innerException);
        }

        throw new IOException(
            $"Failed to safely delete artifact file '{path}'.",
            innerException);
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private const int LinuxCurrentWorkingDirectory = -100;
    private const int LinuxFollowSymbolicLink = 0x400;
    private const int LinuxAnonymousFileFlags = 0x4B0002;
    private const int LinuxDirectoryFlags = 0xB0000;
    private const int LinuxNamedFileFlags = 0xA00C2;
    private const int LinuxInvalidArgument = 22;
    private const int LinuxOperationNotSupported = 95;
    private const int LinuxNoSuchFileOrDirectory = 2;
    private const int LinuxOperationNotPermitted = 1;
    private const int LinuxAccessDenied = 13;
    private const uint LinuxOwnerReadWrite = 0x180;
    private const uint WindowsDelete = 0x00010000;
    private const uint WindowsReadAttributes = 0x00000080;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsDirectoryAttribute = 0x00000010;
    private const uint WindowsReparsePointAttribute = 0x00000400;
    private const uint WindowsNormalizedDosPath = 0;
    private const int WindowsFileNotFound = 2;
    private const int WindowsPathNotFound = 3;
    private const int WindowsAccessDenied = 5;
    private const int WindowsSharingViolation = 32;
    private const string WindowsExtendedPathPrefix = @"\\?\";
    private const string WindowsExtendedUncPrefix = @"\\?\UNC\";

    [DllImport(
        "libc",
        EntryPoint = "linkat",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SuppressMessage(
        "Security",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "POSIX linkat paths are explicitly marshaled as UTF-8.")]
    private static extern int CreateHardLinkUnixFromHandle(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        int flags);

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SuppressMessage(
        "Security",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "POSIX open paths are explicitly marshaled as UTF-8.")]
    private static extern int OpenAnonymousLinuxFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SuppressMessage(
        "Security",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "POSIX open paths are explicitly marshaled as UTF-8.")]
    private static extern int OpenLinuxFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport(
        "libc",
        EntryPoint = "openat",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SuppressMessage(
        "Security",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "POSIX openat paths are explicitly marshaled as UTF-8.")]
    private static extern int OpenLinuxFileAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport(
        "libc",
        EntryPoint = "unlinkat",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [SuppressMessage(
        "Security",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "POSIX unlinkat paths are explicitly marshaled as UTF-8.")]
    private static extern int DeleteLinuxFileAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport(
        "libc",
        EntryPoint = "close",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CloseLinuxFile(int fileDescriptor);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenWindowsFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowsFileInformation(
        SafeFileHandle handle,
        WindowsFileInfoByHandleClass fileInformationClass,
        out WindowsFileBasicInfo fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowsFinalPathName(
        SafeFileHandle handle,
        [Out] char[]? path,
        int capacity,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetFileInformationByHandle",
        SetLastError = true,
        ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowsFileInformation(
        SafeFileHandle handle,
        WindowsFileInfoByHandleClass fileInformationClass,
        ref WindowsFileDispositionInfo fileInformation,
        uint bufferSize);

    private enum WindowsFileInfoByHandleClass
    {
        FileBasicInfo,
        FileDispositionInfo = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }
}

internal sealed class ArtifactWriteLease : IDisposable
{
    private readonly string _rootPath;
    private readonly FileStream _leaseStream;
    private FileStream? _stagingStream;
    private bool _committed;
    private bool _disposed;
    private bool _preservePublishedForReconciliation;

    private ArtifactWriteLease(
        Guid artifactId,
        string rootPath,
        string stagingPath,
        string leasePath,
        FileStream leaseStream)
    {
        ArtifactId = artifactId;
        _rootPath = rootPath;
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
            ArtifactStorageFileSystem.EnsureStagingDirectory(rootPath);
        string normalizedRoot = Path.GetDirectoryName(stagingDirectory) ??
            throw new IOException(
                "The artifact staging directory has no parent directory.");
        string identity = artifactId.ToString("N", CultureInfo.InvariantCulture);
        string stagingPath = Path.Combine(
            stagingDirectory,
            identity + ArtifactStorageFileSystem.StagingFileExtension);
        string leasePath = Path.Combine(
            stagingDirectory,
            identity + ArtifactStorageFileSystem.LeaseFileExtension);
        FileStream leaseStream =
            ArtifactStorageFileSystem.CreateLeaseStream(leasePath);
        return new ArtifactWriteLease(
            artifactId,
            normalizedRoot,
            stagingPath,
            leasePath,
            leaseStream);
    }

    internal FileStream OpenStagingStream()
    {
        if (_stagingStream is not null)
        {
            throw new InvalidOperationException(
                "The artifact staging stream is already open.");
        }

        _stagingStream =
            ArtifactStorageFileSystem.CreateStagingStream(StagingPath);
        return _stagingStream;
    }

    internal void Publish(
        string rootPath,
        string fullPath,
        DateTime publishedAtUtc)
    {
        string publicationPath =
            ArtifactStorageFileSystem.EnsureSafePublicationPath(rootPath, fullPath);

        // Hard-link creation atomically publishes on the same filesystem and fails rather
        // than degrading to a cross-volume copy/delete operation.
        SafeFileHandle stagingHandle = _stagingStream?.SafeFileHandle ??
            throw new InvalidOperationException(
                "The artifact staging stream is not open.");
        File.SetLastWriteTimeUtc(stagingHandle, publishedAtUtc);
        File.SetLastWriteTimeUtc(
            _leaseStream.SafeFileHandle,
            publishedAtUtc);
        ArtifactStorageFileSystem.CreateAtomicHardLink(
            publicationPath,
            StagingPath,
            stagingHandle);
        PublishedPath = publicationPath;
        _stagingStream.Dispose();
        _stagingStream = null;
        ArtifactStorageFileSystem.DeleteFileNoFollow(
            _rootPath,
            StagingPath);
    }

    internal void Commit()
    {
        _committed = true;
        Dispose();
    }

    internal void PreservePublishedForReconciliation() =>
        _preservePublishedForReconciliation = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stagingStream?.Dispose();
        _leaseStream.Dispose();
        if (!_committed)
        {
            TryDelete(StagingPath);
            if (PublishedPath is not null &&
                !_preservePublishedForReconciliation)
            {
                TryDelete(PublishedPath);
            }
        }

        TryDelete(LeasePath);
    }

    private void TryDelete(string path)
    {
        try
        {
            ArtifactStorageFileSystem.DeleteFileNoFollow(
                _rootPath,
                path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
    }
}
