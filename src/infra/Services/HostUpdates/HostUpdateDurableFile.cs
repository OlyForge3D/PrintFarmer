using System.Runtime.InteropServices;
using System.Text;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Small durable-write primitive shared by host-update journal, backup, and recovery state files.</summary>
internal static class HostUpdateDurableFile
{
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    public static void WriteAllTextAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[] bytes = new UTF8Encoding(false).GetBytes(contents);
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        }))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        MoveIntoPlace(temp, path);
    }

    public static async Task CopyFileDurablyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        string temp = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (FileStream destination = new(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        }))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => destination.Flush(flushToDisk: true), cancellationToken).ConfigureAwait(false);
        }

        MoveIntoPlace(temp, destinationPath);
    }

    public static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FlushDirectoryWindows(directory);
            return;
        }

        FlushDirectoryUnix(directory);
    }

    private static void FlushDirectoryWindows(string directory)
    {
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, ".printfarmer-sync-" + Guid.NewGuid().ToString("N"));
        string temp = marker + ".tmp";
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        }))
        {
            stream.Flush(flushToDisk: true);
        }

        MoveIntoPlace(temp, marker);
        File.Delete(marker);
    }

    private static void MoveIntoPlace(string tempPath, string finalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileExW(Path.GetFullPath(tempPath), Path.GetFullPath(finalPath), MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new IOException("atomic_write_through_rename_failed", Marshal.GetLastPInvokeError());
            }

            return;
        }

        File.Move(tempPath, finalPath, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(finalPath) ?? ".");
    }

    private static void FlushDirectoryUnix(string directory)
    {
        int fd = Open(Path.GetFullPath(directory), 0);
        if (fd < 0)
        {
            throw new IOException("directory_sync_unavailable", Marshal.GetLastPInvokeError());
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                throw new IOException("directory_sync_failed", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
