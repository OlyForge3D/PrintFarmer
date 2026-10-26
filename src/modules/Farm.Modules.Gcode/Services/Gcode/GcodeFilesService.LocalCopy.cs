using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// Neutral preparation of captured local bytes. Unlike promotion, this never manufactures
/// lineage or stamps an existing dedup winner. Persistence belongs to the fenced publisher.
/// </summary>
public interface IPrinterFileLibraryIngest
{
    Task<GcodeFile> PrepareAsync(
        Guid fileId, string stagedPath, string fileName, string virtualDirectory,
        string actualSha256, long actualBytes, CancellationToken ct);
}

public partial class GcodeFilesService : IPrinterFileLibraryIngest
{
    public async Task<GcodeFile> PrepareAsync(
        Guid fileId, string stagedPath, string fileName, string virtualDirectory,
        string actualSha256, long actualBytes, CancellationToken ct)
    {
        // Copy establishes owned bytes, not permission to execute them. Queue must
        // validate the captured program against the actual destination before admission.
        string text;
        await using var stream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (actualBytes is <= 0 or > PrinterFileCopyService.MaximumBytes ||
            stream.Length != actualBytes ||
            !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct))
                .Equals(actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PrinterFileCommandException("local_content_changed");
        }

        stream.Position = 0;
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(true, true),
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            text = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            throw new PrinterFileCommandException("unsupported_gcode_encoding");
        }

        if (!HasGcodeFormat(text))
        {
            throw new PrinterFileCommandException("unsupported_gcode_format");
        }

        ct.ThrowIfCancellationRequested();
        GcodeMetadataExtracted metadata = await _metadataExtractor.ExtractMetadataAsync(text);
        ct.ThrowIfCancellationRequested();
        Guid? modelId = await _gcodeRepo.ResolvePrinterModelIdAsync(metadata.PrinterModel, ct);
        string? thumbnailPath = null;
        if (metadata.ThumbnailData is { Length: > 0 and <= 8 * 1024 * 1024 } thumbnail)
        {
            // Use the same metadata thumbnail bytes as the existing local pipeline, but
            // stage under the attempt identity so crashes/dedup can reconcile every file.
            thumbnailPath = stagedPath + ".png";
            await File.WriteAllBytesAsync(thumbnailPath, thumbnail, ct);
        }

        return BuildGcodeFileEntityFromMetadata(
            fileId, fileName, actualSha256, actualBytes, Guid.Empty, virtualDirectory,
            metadata, thumbnailPath, GcodeSource.Upload, Path.GetExtension(fileName),
            resolvedPrinterModelId: modelId);
    }

    private static bool HasGcodeFormat(string text)
    {
        if (text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            return false;
        }

        foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> command = line.TrimStart();
            if (command.Length >= 2 && command[0] is 'G' or 'M' or 'T' or 'g' or 'm' or 't' &&
                char.IsAsciiDigit(command[1]))
            {
                return true;
            }
        }

        return false;
    }
}
