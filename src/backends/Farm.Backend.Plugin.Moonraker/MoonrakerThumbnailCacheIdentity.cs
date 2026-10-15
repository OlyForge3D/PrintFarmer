using System.Text.Json;

namespace Farm.Backend.Plugin.Moonraker;

internal static class MoonrakerThumbnailCacheIdentity
{
    public static (string? ThumbnailUrl, long? FileSize, double? FileModified) ReadFileMetadata(
        string baseUrl,
        JsonElement metadata)
    {
        long? fileSize = metadata.TryGetProperty("size", out JsonElement size) &&
            size.ValueKind == JsonValueKind.Number &&
            size.TryGetInt64(out long sizeValue)
                ? sizeValue
                : null;
        double? fileModified = metadata.TryGetProperty("modified", out JsonElement modified) &&
            modified.ValueKind == JsonValueKind.Number &&
            modified.TryGetDouble(out double modifiedValue) &&
            double.IsFinite(modifiedValue)
                ? modifiedValue
                : null;

        string? thumbnailUrl = null;
        if (metadata.TryGetProperty("thumbnails", out JsonElement thumbnails) &&
            thumbnails.ValueKind == JsonValueKind.Array)
        {
            JsonElement largest = thumbnails.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .OrderByDescending(item =>
                    (long)(item.TryGetProperty("width", out JsonElement width) && width.TryGetInt32(out int w) ? w : 0) *
                    (item.TryGetProperty("height", out JsonElement height) && height.TryGetInt32(out int h) ? h : 0))
                .FirstOrDefault();
            if (largest.ValueKind == JsonValueKind.Object &&
                largest.TryGetProperty("relative_path", out JsonElement relativePath) &&
                relativePath.ValueKind == JsonValueKind.String)
            {
                thumbnailUrl = CreateThumbnailUrl(baseUrl, relativePath.GetString()!);
            }
        }

        return (thumbnailUrl, fileSize, fileModified);
    }

    public static string? CreateThumbnailUrl(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.Split('/').Any(segment => segment is "." or ".."))
        {
            return null;
        }

        string escapedPath = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(new Uri(baseUrl), $"server/files/gcodes/{escapedPath}").AbsoluteUri;
    }

    public static string? Create(double? startTime, long? fileSize, double? modified)
    {
        string? startIdentity = startTime is { } value && double.IsFinite(value)
            ? $"start:{Math.Round(value, MidpointRounding.AwayFromZero):0}"
            : null;
        string? fileIdentity = fileSize is not null || modified is not null
            ? $"file:{fileSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{modified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
            : null;

        return (startIdentity, fileIdentity) switch
        {
            ({ } start, { } file) => $"{start}|{file}",
            ({ } start, null) => start,
            (null, { } file) => file,
            _ => null,
        };
    }
}
