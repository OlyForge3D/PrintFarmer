using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Contract for querying the Printables GraphQL API for public model data.
/// </summary>
public interface IPrintablesGraphQLClient
{
    Task<IReadOnlyList<PrintablesCollectionDto>> GetUserCollectionsAsync(string userId, CancellationToken ct);

    Task<PrintablesPagedResultDto<PrintablesModelCardDto>> GetUserModelsAsync(
        string userId,
        int limit,
        string? cursor,
        string? ordering,
        CancellationToken ct);

    Task<PrintablesPagedResultDto<PrintablesModelCardDto>> SearchModelsAsync(
        string query,
        int limit,
        string? cursor,
        CancellationToken ct);

    Task<PrintablesPrintProfileDto> GetPrintProfileAsync(string printId, CancellationToken ct);

    Task<PrintablesPreviewDto> FetchPreviewAsync(string modelId, string sourceUrl, CancellationToken ct);

    Task<string> GetStlDownloadUrlAsync(string modelId, string fileId, CancellationToken ct);

    Task<byte[]> DownloadFileAsync(string downloadUrl, CancellationToken ct);
}
