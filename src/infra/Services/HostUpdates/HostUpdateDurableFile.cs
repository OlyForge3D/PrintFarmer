using System.Text;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Small durable-write primitive shared by host-update journal, backup, and recovery state files.</summary>
internal static class HostUpdateDurableFile
{
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

        File.Move(temp, path, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(path) ?? ".");
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

        File.Move(temp, destinationPath, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
    }

    public static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using FileStream stream = new(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
