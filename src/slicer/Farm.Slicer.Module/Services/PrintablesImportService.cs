using System.Text.RegularExpressions;
using Farm.Slicer.Module.Dtos;
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
    // Matches:
    //   https://www.printables.com/model/12345
    //   https://www.printables.com/model/12345-my-cool-model
    //   https://printables.com/model/12345-slug
    private static readonly Regex _modelUrlPattern =
        new(@"printables\.com/model/(\d+)(?:-[^/?#]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        Match match = _modelUrlPattern.Match(url);
        if (!match.Success)
        {
            throw new ArgumentException(
                $"'{url}' is not a recognised Printables model URL. " +
                "Expected format: https://www.printables.com/model/{{id}} or https://www.printables.com/model/{{id}}-{{slug}}",
                nameof(url));
        }

        return match.Groups[1].Value;
    }
}
