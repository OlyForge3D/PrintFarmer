using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Certified lightweight, authenticated per-file metadata/image reads. Implementations must
/// never download G-code, follow redirects, or accept a caller-supplied thumbnail URL.
/// </summary>
public interface ISupportsPrinterFilePreview
{
    /// <summary>Reads only native metadata and an optional bounded image for the exact observation.</summary>
    Task<PrinterFilePreviewContent> GetFilePreviewAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
        CancellationToken ct);
}

/// <summary>Absent provider metadata remains null; it is never inferred from file size or name.</summary>
public sealed record PrinterFilePreviewContent(
    double? PrintTimeSeconds, double? LayerHeightMm, byte[]? Image = null, string? ContentType = null);

/// <summary>Shared bounded transport and observation validation for lightweight preview adapters.</summary>
public static class PrinterFilePreviewTransport
{
    public const int MaximumMetadataBytes = 256 * 1024;
    public const int MaximumImageBytes = 2 * 1024 * 1024;

    /// <summary>Uses the certified redirect-disabled streaming transport, including length checks.</summary>
    public static async Task<byte[]> ReadAsync(
        HttpClient http, Uri uri, PrinterCredential? credential, int maximumBytes, CancellationToken ct)
    {
        using MemoryStream buffer = new();
        await PrinterFileCopyTransport.CopyAsync(http, uri, credential, buffer, maximumBytes, ct)
            .ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Rejects changed or missing provider identity rather than mislabeling a capture.</summary>
    public static void ValidateObservation(PrinterFileInventoryEntry expected, PrinterFileInventoryEntry actual)
    {
        if (expected.StorageRoot != actual.StorageRoot || expected.RelativePath != actual.RelativePath ||
            expected.SizeBytes != actual.SizeBytes || expected.ModifiedValue != actual.ModifiedValue ||
            expected.ProviderVersion != actual.ProviderVersion || expected.ProviderHash != actual.ProviderHash)
        {
            throw new PrinterFileCopyException("source_changed");
        }
    }

    /// <summary>Ignores invalid, negative, non-finite or absent provider measurements.</summary>
    public static double? PositiveNumber(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) &&
        double.IsFinite(number) && number > 0 ? number : null;

    /// <summary>Only inert, signature-checked raster images may be served from the cache.</summary>
    public static bool IsValidImage(byte[] image, string? contentType) =>
        image.Length is > 0 and <= MaximumImageBytes && contentType switch
        {
            "image/png" => image.AsSpan().StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => image.Length >= 3 && image[0] == 0xff && image[1] == 0xd8 && image[2] == 0xff,
            "image/webp" => image.Length >= 12 && image.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                image.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
}
