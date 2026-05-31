namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Preview metadata returned by the Printables import preview endpoint.
/// Contains enough information for the user to choose which file to import.
/// </summary>
/// <param name="ModelId">Printables model ID extracted from the URL.</param>
/// <param name="Name">Model display name.</param>
/// <param name="Creator">Creator/author public username.</param>
/// <param name="License">License display name (e.g., "CC BY 4.0").</param>
/// <param name="ThumbnailUrl">Absolute URL to the model thumbnail image.</param>
/// <param name="SourceUrl">Original Printables URL.</param>
/// <param name="Files">Ordered list of downloadable files in this model.</param>
public sealed record PrintablesPreviewDto(
    string ModelId,
    string Name,
    string Creator,
    string? License,
    string? ThumbnailUrl,
    string SourceUrl,
    IReadOnlyList<PrintablesFileEntryDto> Files);

/// <summary>
/// A single downloadable file in a Printables model.
/// </summary>
/// <param name="Id">Printables file ID.</param>
/// <param name="Name">Original filename.</param>
/// <param name="FileSize">File size in bytes (0 if unknown).</param>
public sealed record PrintablesFileEntryDto(
    string Id,
    string Name,
    long FileSize);
