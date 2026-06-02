using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Implements <see cref="IPrintablesImportService"/> by parsing Printables URLs and
/// delegating metadata fetches to <see cref="PrintablesGraphQLClient"/>.
/// </summary>
/// <remarks>
/// Preview fetches model metadata without any DB writes.
/// Attribution persistence (<see cref="PersistAttributionAsync"/>) was added in #351 and
/// writes <c>SourceUrl</c>, <c>SourceCreator</c>, <c>SourceLicense</c>, and <c>ImportedAt</c>
/// onto an existing <see cref="Farm.Slicer.Module.Domain.Model3D"/> record.
/// </remarks>
public sealed class PrintablesImportService(
    PrintablesGraphQLClient graphQlClient,
    IModel3DFileService model3DFileService,
    ILogger<PrintablesImportService> logger) : IPrintablesImportService
{
    private static readonly string[] AllowedHosts = ["printables.com", "www.printables.com"];

    private readonly PrintablesGraphQLClient _graphQlClient = graphQlClient;
    private readonly IModel3DFileService _model3DFileService = model3DFileService;
    private readonly ILogger<PrintablesImportService> _logger = logger;

    /// <inheritdoc />
    public async Task<PrintablesPreviewDto> PreviewAsync(string printablesUrl, CancellationToken ct)
    {
        string modelId = ParseModelId(printablesUrl);

        _logger.LogInformation("Fetching Printables preview for model ID {ModelId} from {Url}", modelId, printablesUrl);

        try
        {
            PrintablesPreviewDto preview = await _graphQlClient.FetchPreviewAsync(modelId, printablesUrl, ct);
            _logger.LogInformation("Preview fetched for Printables model {ModelId}: '{Name}'", modelId, preview.Name);
            return preview;
        }
        catch (PrintablesApiException ex)
        {
            _logger.LogWarning(ex, "Printables API error for model {ModelId}", modelId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Model3DUploadResultDto>> ImportAsync(string printablesUrl, IReadOnlyCollection<string>? fileIds, CancellationToken ct)
    {
        string modelId = ParseModelId(printablesUrl);

        _logger.LogInformation("Importing Printables model ID {ModelId} from {Url}", modelId, printablesUrl);

        PrintablesPreviewDto preview = await _graphQlClient.FetchPreviewAsync(modelId, printablesUrl, ct);
        List<PrintablesFileEntryDto> selectedFiles = SelectFilesForImport(preview, fileIds);
        List<Model3DUploadResultDto> importedModels = new(selectedFiles.Count);

        foreach (PrintablesFileEntryDto selectedFile in selectedFiles)
        {
            string downloadUrl = await _graphQlClient.GetStlDownloadUrlAsync(modelId, selectedFile.Id, ct);
            byte[] fileBytes = await _graphQlClient.DownloadFileAsync(downloadUrl, ct);

            using MemoryStream stream = new(fileBytes, writable: false);
            FormFile formFile = new(stream, 0, stream.Length, "file", selectedFile.Name)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/octet-stream",
            };

            Model3DUploadResultDto uploadedModel = await _model3DFileService.UploadModelAsync(formFile, ct);
            await _model3DFileService.SetAttributionAsync(
                uploadedModel.Id,
                sourceUrl: printablesUrl,
                sourceCreator: preview.Creator,
                sourceLicense: preview.License,
                importedAt: DateTime.UtcNow,
                ct);

            importedModels.Add(uploadedModel);
        }

        _logger.LogInformation(
            "Imported {ImportedCount} Printables file(s) from model {ModelId}",
            importedModels.Count,
            modelId);

        return importedModels;
    }

    /// <inheritdoc />
    public async Task PersistAttributionAsync(Guid modelId, string printablesUrl, CancellationToken ct)
    {
        string parsedModelId = ParseModelId(printablesUrl);

        _logger.LogInformation(
            "Fetching attribution for Printables model {ModelId} to persist on file record {FileId}",
            parsedModelId, modelId);

        PrintablesPreviewDto preview = await _graphQlClient.FetchPreviewAsync(parsedModelId, printablesUrl, ct);

        await _model3DFileService.SetAttributionAsync(
            modelId,
            sourceUrl: printablesUrl,
            sourceCreator: preview.Creator,
            sourceLicense: preview.License,
            importedAt: DateTime.UtcNow,
            ct);

        _logger.LogInformation(
            "Attribution persisted for model record {FileId}: creator={Creator}, license={License}",
            modelId, preview.Creator, preview.License);
    }

    private static List<PrintablesFileEntryDto> SelectFilesForImport(PrintablesPreviewDto preview, IReadOnlyCollection<string>? fileIds)
    {
        if (preview.Files.Count == 0)
        {
            throw new ArgumentException("The selected Printables model has no downloadable STL files.", nameof(preview));
        }

        string[] normalizedFileIds = fileIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (normalizedFileIds.Length == 0)
        {
            return [.. preview.Files];
        }

        HashSet<string> selectedIdSet = normalizedFileIds.ToHashSet(StringComparer.Ordinal);
        HashSet<string> availableIdSet = preview.Files.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        string[] missingIds = normalizedFileIds.Where(id => !availableIdSet.Contains(id)).ToArray();

        if (missingIds.Length > 0)
        {
            throw new ArgumentException(
                $"The selected Printables file IDs were not found in the model: {string.Join(", ", missingIds)}.",
                nameof(fileIds));
        }

        return [.. preview.Files.Where(file => selectedIdSet.Contains(file.Id))];
    }

    /// <summary>
    /// Extracts the numeric model ID from a Printables URL.
    /// </summary>
    /// <param name="url">Raw URL string supplied by the user.</param>
    /// <returns>The numeric model ID as a string.</returns>
    /// <exception cref="ArgumentException">The URL does not match the expected Printables model pattern.</exception>
    public static string ParseModelId(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL must not be empty.", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/model/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{url}' is not a recognised Printables model URL. " +
                "Expected format: https://www.printables.com/model/{{id}} or https://www.printables.com/model/{{id}}-{{slug}}",
                nameof(url));
        }

        ReadOnlySpan<char> modelPath = uri.AbsolutePath.AsSpan("/model/".Length);
        int end = modelPath.IndexOfAny('-', '/');
        ReadOnlySpan<char> idSpan = end >= 0 ? modelPath[..end] : modelPath;

        if (idSpan.IsEmpty)
        {
            throw new ArgumentException(
                $"'{url}' is not a recognised Printables model URL. " +
                "Expected format: https://www.printables.com/model/{{id}} or https://www.printables.com/model/{{id}}-{{slug}}",
                nameof(url));
        }

        foreach (char c in idSpan)
        {
            if (!char.IsDigit(c))
            {
                throw new ArgumentException(
                    $"'{url}' is not a recognised Printables model URL. " +
                    "Expected format: https://www.printables.com/model/{{id}} or https://www.printables.com/model/{{id}}-{{slug}}",
                    nameof(url));
            }
        }

        return idSpan.ToString();
    }
}
