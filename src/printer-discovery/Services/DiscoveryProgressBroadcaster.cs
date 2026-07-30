using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PrinterDiscovery.Services;

/// <summary>
/// Sends discovery events to the API over the authenticated internal ingestion boundary.
/// </summary>
public interface IDiscoveryProgressBroadcaster
{
    /// <summary>
    /// Broadcasts discovery progress to the API.
    /// </summary>
    Task BroadcastProgressAsync(DiscoveryProgressDto progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a discovered printer to the API.
    /// </summary>
    Task BroadcastPrinterFoundAsync(
        InternalDiscoveryPrinterFoundDto printerFound,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts discovery completion to the API.
    /// </summary>
    Task BroadcastCompletedAsync(DiscoveryCompletedDto completed, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DiscoveryProgressBroadcaster : IDiscoveryProgressBroadcaster
{
    private const string ServiceKeyHeaderName = "X-Discovery-Service-Key";

    /// <summary>Request path used for progress events. Kept as a constant so tests can pin it to the controller route template.</summary>
    public const string ProgressPath = "api/internal/discovery/events/progress";

    /// <summary>Request path used for printer-found events. Kept as a constant so tests can pin it to the controller route template.</summary>
    public const string PrinterFoundPath = "api/internal/discovery/events/printer-found";

    /// <summary>Request path used for completion events. Kept as a constant so tests can pin it to the controller route template.</summary>
    public const string CompletedPath = "api/internal/discovery/events/completed";

    private readonly HttpClient _httpClient;
    private readonly ILogger<DiscoveryProgressBroadcaster> _logger;
    private readonly string? _sharedKey;

    /// <summary>
    /// Initializes the authenticated discovery event broadcaster.
    /// </summary>
    public DiscoveryProgressBroadcaster(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DiscoveryProgressBroadcaster> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
        _sharedKey = configuration["Discovery:SharedKey"];

        string serviceUrl = configuration["Discovery:ApiBaseUrl"] ?? "http://api:5245";
        _httpClient.BaseAddress = new Uri(serviceUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    /// <inheritdoc />
    public Task BroadcastProgressAsync(
        DiscoveryProgressDto progress,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ProgressPath,
            new InternalDiscoveryProgressDto(
                progress.SessionId,
                progress.TotalIps,
                progress.ScannedIps,
                progress.PrintersFound,
                progress.PrintersExcluded,
                progress.ProgressPercentage,
                progress.Status,
                progress.Message,
                progress.AutoDetectedNetworks),
            cancellationToken);

    /// <inheritdoc />
    public Task BroadcastPrinterFoundAsync(
        InternalDiscoveryPrinterFoundDto printerFound,
        CancellationToken cancellationToken = default) =>
        SendAsync(PrinterFoundPath, printerFound, cancellationToken);

    /// <inheritdoc />
    public Task BroadcastCompletedAsync(
        DiscoveryCompletedDto completed,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            CompletedPath,
            new InternalDiscoveryCompletedDto(
                completed.SessionId,
                completed.TotalPrintersFound,
                completed.TotalPrintersExcluded,
                completed.Duration,
                completed.WasCancelled,
                completed.AutoDetectedNetworks),
            cancellationToken);

    private async Task SendAsync<T>(
        string requestUri,
        T payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sharedKey))
        {
            throw new InvalidOperationException(
                "Discovery event authentication is unavailable because Discovery:SharedKey is not configured.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(payload),
        };
        _ = request.Headers.TryAddWithoutValidation(ServiceKeyHeaderName, _sharedKey);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish discovery event to {RequestUri}",
                requestUri);
            throw;
        }
    }
}
