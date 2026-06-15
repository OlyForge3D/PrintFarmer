using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Provides preview metadata for a Printables.com model URL, without persisting anything.
/// Import (persist to DB) is handled separately in #351.
/// </summary>
public interface IPrintablesImportService
{
    /// <summary>
    /// Queries the Printables public GraphQL API for model metadata.
    /// </summary>
    /// <param name="printablesUrl">
    /// A Printables model URL — accepts both
    /// <c>https://www.printables.com/model/{id}-{slug}</c> and
    /// <c>https://www.printables.com/model/{id}</c> forms.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preview DTO ready to display in the import modal.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="printablesUrl"/> cannot be parsed as a valid Printables model URL.
    /// </exception>
    /// <exception cref="PrintablesApiException">
    /// Thrown when the Printables GraphQL API returns an error or the model is not found.
    /// </exception>
    Task<PrintablesPreviewDto> PreviewAsync(string printablesUrl, CancellationToken ct);

    /// <summary>
    /// Imports one or more files from a Printables model into the local 3D model library.
    /// </summary>
    /// <param name="printablesUrl">The canonical Printables model page URL.</param>
    /// <param name="fileIds">
    /// Optional list of selected Printables file IDs.
    /// When <see langword="null"/> or empty, all files in the model are imported.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The uploaded model records created by the import.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="printablesUrl"/> is invalid or <paramref name="fileIds"/>
    /// contains IDs that do not exist in the model.
    /// </exception>
    /// <exception cref="PrintablesApiException">
    /// Thrown when Printables metadata, download-link resolution, or file download fails.
    /// </exception>
    Task<IReadOnlyList<Model3DUploadResultDto>> ImportAsync(string printablesUrl, IReadOnlyCollection<string>? fileIds, CancellationToken ct);

    /// <summary>
    /// Sets attribution metadata on an existing <see cref="Farm.Slicer.Module.Domain.Model3D"/> record
    /// that was uploaded via <see cref="IModel3DFileService.UploadModelAsync"/>.
    /// Call this after upload to associate the model with its Printables source.
    /// </summary>
    /// <param name="modelId">The ID of the already-uploaded model record.</param>
    /// <param name="printablesUrl">The canonical Printables model page URL.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PersistAttributionAsync(Guid modelId, string printablesUrl, CancellationToken ct);

    /// <summary>
    /// Resolves a Printables username and returns the user's public collections.
    /// </summary>
    /// <param name="username">Printables handle or public username.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrintablesCollectionsBrowseDto> BrowseCollectionsAsync(string username, CancellationToken ct);

    /// <summary>
    /// Returns cursor-paginated models uploaded by a Printables user.
    /// </summary>
    /// <param name="username">Printables handle or public username.</param>
    /// <param name="limit">Page size (1-50).</param>
    /// <param name="cursor">Optional cursor from a previous page.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrintablesCursorPageDto> BrowseUserModelsAsync(string username, int limit, string? cursor, CancellationToken ct);

    /// <summary>
    /// Returns cursor-paginated models within a Printables collection.
    /// </summary>
    /// <param name="collectionId">Printables collection ID.</param>
    /// <param name="limit">Page size (1-50).</param>
    /// <param name="cursor">Optional cursor from a previous page.</param>
    /// <param name="query">Optional search filter scoped to this collection.</param>
    /// <param name="ordering">Optional collection sort key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrintablesCursorPageDto> BrowseCollectionModelsAsync(string collectionId, int limit, string? cursor, string? query, string? ordering, CancellationToken ct);

    /// <summary>
    /// Searches Printables models by keyword.
    /// </summary>
    /// <param name="query">Search text.</param>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Page size (1-50).</param>
    /// <param name="ordering">Optional search ordering key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrintablesSearchResultsDto> SearchModelsAsync(string query, int offset, int limit, string? ordering, CancellationToken ct);
}
