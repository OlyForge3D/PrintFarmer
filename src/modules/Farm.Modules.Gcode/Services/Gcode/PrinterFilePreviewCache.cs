using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>A private local cache, never a library asset or a static-file endpoint.</summary>
public sealed class PrinterFilePreviewCache(
    string directory, int maximumEntries = 256, long maximumBytes = 64 * 1024 * 1024)
{
    private const int MaximumEntryBytes = 3 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Includes configuration and revision in addition to printer/path/size/mtime identity.</summary>
    public static string Key(PrinterFileLocation location, string configurationFingerprint) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            location.SourcePrinterId,
            location.StorageRoot,
            location.RelativePath,
            location.SizeBytes,
            location.ModifiedValue,
            location.ProviderVersion,
            location.ProviderDigest,
            location.ObservationRevision,
            location.ConfigurationGeneration,
            location.MutationGeneration,
            configurationFingerprint,
        })));

    /// <summary>Reads bounded, unexpired content. Authorization is always the caller's responsibility.</summary>
    public async Task<PrinterFilePreviewCapture?> GetAsync(string key, CancellationToken ct)
    {
        string path = EntryPath(key);
        await using FileStream gate = await LockAsync(ct);
        if (!File.Exists(path))
        {
            return null;
        }

        FileInfo info = new(path);
        if (info.Length > MaximumEntryBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            File.Delete(path);
            return null;
        }

        try
        {
            PrinterFilePreviewCapture? capture = JsonSerializer.Deserialize<PrinterFilePreviewCapture>(
                await File.ReadAllBytesAsync(path, ct));
            if (capture is not null && capture.CapturedAtUtc > DateTime.UtcNow - Lifetime &&
                capture.CapturedAtUtc <= DateTime.UtcNow && IsValid(capture.Content))
            {
                return capture;
            }
        }
        catch (JsonException)
        {
            // A crash while writing is a cache miss, not permission to serve partial bytes.
        }

        File.Delete(path);
        return null;
    }

    /// <summary>Serializes writers across processes and evicts oldest entries before exceeding either bound.</summary>
    public async Task PutAsync(string key, PrinterFilePreviewCapture capture, CancellationToken ct)
    {
        string path = EntryPath(key);
        if (!IsValid(capture.Content))
        {
            throw new InvalidDataException("Invalid preview cache content.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(capture);
        if (bytes.Length > MaximumEntryBytes || bytes.Length > maximumBytes || maximumEntries < 1)
        {
            throw new InvalidDataException("Preview cache size limit.");
        }

        await using FileStream gate = await LockAsync(ct);
        File.Delete(path);
        List<FileInfo> entries = new DirectoryInfo(directory).EnumerateFiles("*.json")
            .OrderBy(x => x.LastWriteTimeUtc).ToList();
        long total = entries.Sum(x => x.Length);
        int count = entries.Count;
        foreach (FileInfo entry in entries)
        {
            if (entry.LastWriteTimeUtc > DateTime.UtcNow - Lifetime &&
                count < maximumEntries && total <= maximumBytes - bytes.Length)
            {
                break;
            }

            long length = entry.Length;
            entry.Delete();
            total -= length;
            count--;
        }

        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    private static bool IsValid(PrinterFilePreviewContent? content) =>
        content is not null && (content.Image is null ? content.ContentType is null :
            PrinterFilePreviewTransport.IsValidImage(content.Image, content.ContentType)) &&
        (content.PrintTimeSeconds is null || (double.IsFinite(content.PrintTimeSeconds.Value) &&
            content.PrintTimeSeconds > 0)) &&
        (content.LayerHeightMm is null || (double.IsFinite(content.LayerHeightMm.Value) && content.LayerHeightMm > 0)) &&
        content.SizeBytes is not < 0 && (content.Metadata is null || content.Metadata.IsValid());

    private string EntryPath(string key)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("Invalid preview cache key.", nameof(key));
        }

        return Path.Join(directory, key + ".json");
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    Path.Join(directory, ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 40)
            {
                attempt++;
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }
    }
}

/// <summary>Time of the actual native metadata/image fetch, not time of browsing the catalog.</summary>
public sealed record PrinterFilePreviewCapture(DateTime CapturedAtUtc, PrinterFilePreviewContent Content);
