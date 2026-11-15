using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Independent acquisition outcome for metadata or an image, not file availability.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrinterFileEnrichmentState>))]
public enum PrinterFileEnrichmentState
{
    Pending,
    Ready,
    Partial,
    Absent,
    Unsupported,
    Deferred,
    Failed,
}

/// <summary>
/// Combined background-only acquisition under a confirmed-idle catalog lease.
/// Native metadata is preferred; fallback may read bounded source regions, never import a file.
/// </summary>
public interface ISupportsPrinterFileEnrichment
{
    /// <summary>Acquires missing detail and optionally an image for the exact source observation.</summary>
    Task<PrinterFileEnrichmentContent> EnrichFileAsync(
        string baseUrl, PrinterFileInventoryEntry observation, PrinterCredential? credential,
        bool captureThumbnail, CancellationToken ct);
}

/// <summary>One bounded acquisition, with independent outcomes so an image failure cannot discard metadata.</summary>
public sealed record PrinterFileEnrichmentContent(
    PrinterPrintMetadata? Metadata,
    PrinterFileEnrichmentState MetadataState,
    PrinterFileEnrichmentState ThumbnailState,
    byte[]? Image = null,
    string? ContentType = null,
    string? MetadataReason = null,
    string? ThumbnailReason = null,
    string? MetadataSource = null,
    long ResponseBytes = 0);

/// <summary>Hard safety bounds shared by backend adapters and the background scheduler.</summary>
public static class PrinterFileEnrichmentLimits
{
    public const int ExtractorVersion = 1;
    public const int MetadataBytes = 256 * 1024;
    public const int MaximumImageBytes = 2 * 1024 * 1024;
    public const int TextRegionBytes = 1024 * 1024;
    public const int BinaryPrefixBytes = 4 * 1024 * 1024;
    public const int MaximumDecodedBytes = 4 * 1024 * 1024;
    public const int MaximumRequests = 8;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public const int MaximumImageEdge = 4096;
    public const long MaximumImagePixels = 4_194_304;
    public const int MaximumFilesPerSlice = 8;
    public const int MaximumBytesPerSlice = 16 * 1024 * 1024;
    public static readonly TimeSpan FileDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RevalidationInterval = TimeSpan.FromHours(24);
}
