namespace Farm.Web.Api.Services;

/// <summary>
/// Service for tracking user storage quota usage for G-code uploads.
/// </summary>
public interface IGcodeUploadQuotaService
{
    /// <summary>
    /// Attempts to add storage usage for a user, respecting quota limits.
    /// </summary>
    /// <param name="userId">The user ID to track usage for.</param>
    /// <param name="bytes">Number of bytes to add to usage.</param>
    /// <param name="usedBytes">Output: current used bytes after addition (or before if quota exceeded).</param>
    /// <param name="limitBytes">Output: the user's quota limit in bytes.</param>
    /// <returns>True if usage was added successfully; false if quota would be exceeded.</returns>
    bool TryAddUsage(string userId, long bytes, out long usedBytes, out long limitBytes);
}
