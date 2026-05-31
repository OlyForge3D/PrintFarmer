using System.Text.RegularExpressions;
using Farm.Slicer.Module.Dtos;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Implements <see cref="IPrintablesImportService"/> by parsing Printables URLs and
/// delegating metadata fetches to <see cref="PrintablesGraphQLClient"/>.
/// </summary>
/// <remarks>
/// Only the preview path is implemented in this issue (#349).
/// Import (DB persistence of attribution) lands in #351.
/// </remarks>
public sealed class PrintablesImportService(
    PrintablesGraphQLClient graphQlClient,
    ILogger<PrintablesImportService> logger) : IPrintablesImportService
{
    // Anchored pattern: requires path to start with /model/{digits}
    private static readonly Regex _modelPathPattern =
        new(@"^/model/(\d+)(?:-[^/?#]*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> _allowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "printables.com",
        "www.printables.com",
    };

    private readonly PrintablesGraphQLClient _graphQlClient = graphQlClient;
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

    /// <summary>
    /// Extracts the numeric model ID from a Printables URL.
    /// Uses <see cref="Uri"/> for host extraction to prevent substring domain spoofing,
    /// then applies an anchored regex on the path component.
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

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"'{url}' is not a valid HTTP(S) URL.",
                nameof(url));
        }

        if (!_allowedHosts.Contains(uri.Host))
        {
            throw new ArgumentException(
                $"'{url}' is not a recognised Printables model URL. " +
                "Expected host: printables.com or www.printables.com",
                nameof(url));
        }

        Match match = _modelPathPattern.Match(uri.AbsolutePath);
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
