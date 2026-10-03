using System.Security.Cryptography;
using System.Text;

namespace Farm.Infrastructure;

/// <summary>
/// Builds stable, versioned same-origin URLs for active printer job thumbnails.
/// </summary>
public static class PrinterThumbnailUrl
{
    /// <summary>
    /// Returns a relative URL only when an active job has a thumbnail target.
    /// The target is hashed and is never included in the returned URL.
    /// </summary>
    public static string? Create(Guid printerId, string? state, string? jobName, string? thumbnailUrl)
    {
        if ((state is null ||
             (!state.Equals("printing", StringComparison.OrdinalIgnoreCase) &&
              !state.Equals("paused", StringComparison.OrdinalIgnoreCase))) ||
            string.IsNullOrWhiteSpace(jobName) ||
            string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return null;
        }

        return $"/api/printers/{printerId:D}/current-job/thumbnail?v={GetCacheToken(jobName, thumbnailUrl)}";
    }

    /// <summary>
    /// Returns the opaque cache token for a private backend thumbnail target.
    /// </summary>
    public static string GetCacheToken(string jobName, string thumbnailUrl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{jobName}\n{thumbnailUrl}")))[..16]
            .ToLowerInvariant();
}
