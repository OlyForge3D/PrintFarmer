namespace Farm.Slicer.Module.Services;

/// <summary>
/// Parses imported slicer profile JSON, extracts core metadata and produces a sanitized
/// deterministic JSON string plus a stable SHA256 hash for deduplication.
/// </summary>
public interface ISlicerProfileParsingService
{
    /// <summary>
    /// Parses raw profile JSON, extracts all settings as flat key-value pairs,
    /// removes volatile keys and returns sanitized JSON, settings JSON, and hash.
    /// </summary>
    /// <param name="rawJson">The raw profile JSON string.</param>
    /// <returns>Tuple of (SanitizedRawJson, SettingsJson, SHA256 Hash).</returns>
    /// <exception cref="ArgumentException">Thrown when rawJson is null or empty.</exception>
    (string SanitizedRawJson, string SettingsJson, string Hash) ParseAndPrepare(string rawJson);
}
