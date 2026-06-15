using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Contract for querying the Printables GraphQL API for public model data.
/// </summary>
public interface IPrintablesGraphQLClient
{
    Task<PrintablesUserProfileDto> ResolveUserProfileAsync(string username, CancellationToken ct);

    Task<IReadOnlyList<PrintablesCollectionDto>> GetUserCollectionsAsync(string username, CancellationToken ct);

    Task<PrintablesPagedResultDto<PrintablesModelCardDto>> GetUserModelsAsync(
        string username,
        int limit,
        string? cursor,
        string? ordering,
        CancellationToken ct);

    Task<PrintablesPagedResultDto<PrintablesModelCardDto>> GetCollectionModelsAsync(
        string collectionId,
        int limit,
        string? cursor,
        string? query,
        string? ordering,
        CancellationToken ct);

    Task<PrintablesSearchResultsDto> SearchModelsAsync(
        string query,
        int offset,
        int limit,
        string? ordering,
        CancellationToken ct);

    Task<PrintablesPrintProfileDto> GetPrintProfileAsync(string printId, CancellationToken ct);

    Task<PrintablesPreviewDto> FetchPreviewAsync(string modelId, string sourceUrl, CancellationToken ct);

    Task<string> GetStlDownloadUrlAsync(string modelId, string fileId, CancellationToken ct);

    Task<byte[]> DownloadFileAsync(string downloadUrl, CancellationToken ct);
}
