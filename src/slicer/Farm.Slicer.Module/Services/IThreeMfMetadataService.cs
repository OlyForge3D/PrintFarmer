using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service for extracting metadata from 3MF files.
/// </summary>
public interface IThreeMfMetadataService
{
    /// <summary>
    /// Extracts metadata from a 3MF file.
    /// </summary>
    /// <param name="filePath">Path to the 3MF file on disk.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extracted metadata, or null if the file is not a valid 3MF.</returns>
    Task<ThreeMfMetadataDto?> ExtractMetadataAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Extracts metadata from a 3MF file stream.
    /// </summary>
    /// <param name="stream">Stream containing 3MF file data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extracted metadata, or null if the stream is not a valid 3MF.</returns>
    Task<ThreeMfMetadataDto?> ExtractMetadataAsync(Stream stream, CancellationToken ct);
}
