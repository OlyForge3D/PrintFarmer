namespace Farm.Backend.Plugin.Moonraker;

internal static class MoonrakerThumbnailCacheIdentity
{
    public static string? CreateThumbnailUrl(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.Split('/').Any(segment => segment is "." or ".."))
        {
            return null;
        }

        string escapedPath = string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(new Uri(baseUrl), $"server/files/gcodes/{escapedPath}").ToString();
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
