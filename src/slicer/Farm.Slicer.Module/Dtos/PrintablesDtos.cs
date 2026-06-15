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

/// <summary>
/// Public collection summary for a Printables user.
/// </summary>
public sealed record PrintablesCollectionDto(
    string Id,
    string Name,
    string? Slug,
    string? Description,
    int ModelCount,
    string? ThumbnailUrl);

/// <summary>
/// Lightweight model card used for list/search surfaces.
/// </summary>
public sealed record PrintablesModelCardDto(
    string Id,
    string Name,
    string? Slug,
    string? Description,
    string? Creator,
    string? ThumbnailUrl,
    int LikeCount,
    int DownloadCount);

/// <summary>
/// Cursor-based paged response returned by Printables listing/search queries.
/// </summary>
public sealed record PrintablesPagedResultDto<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasNextPage);

/// <summary>
/// Full profile details for a single Printables model.
/// </summary>
public sealed record PrintablesPrintProfileDto(
    string Id,
    string Name,
    string? Slug,
    string? Description,
    string Creator,
    string? License,
    string? ThumbnailUrl,
    IReadOnlyList<PrintablesFileEntryDto> Files);

/// <summary>
/// Request body for <c>POST api/3d-models/printables/attribution</c>.
/// Associates an already-uploaded model record with its Printables source.
/// </summary>
public sealed class PersistAttributionRequestDto
{
    /// <summary>Gets or sets the ID of the uploaded model record to annotate.</summary>
    public Guid ModelId { get; set; }

    /// <summary>Gets or sets the canonical Printables model page URL.</summary>
    public string PrintablesUrl { get; set; } = string.Empty;
}
