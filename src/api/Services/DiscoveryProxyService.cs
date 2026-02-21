using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Discovery;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services;

/// <summary>
/// Proxies discovery requests to the printer-discovery microservice.
/// The microservice handles actual network scanning and broadcasts progress via SignalR.
/// </summary>
public class DiscoveryProxyService : IDiscoveryProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHubContext<PrinterHub> _hubContext;
    private readonly IDiscoveryProgressCache _progressCache;
    private readonly ISettingsService _settingsService;
    private readonly IUnifiedLoggingService _logger;
    private readonly string _discoveryServiceUrl;

    public DiscoveryProxyService(
        IHttpClientFactory httpClientFactory,
        IHubContext<PrinterHub> hubContext,
        IDiscoveryProgressCache progressCache,
        ISettingsService settingsService,
        IUnifiedLoggingService logger,
        IConfiguration config)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _progressCache = progressCache ?? throw new ArgumentNullException(nameof(progressCache));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(config);

        // Default to Docker Compose service name, can be overridden via config
        _discoveryServiceUrl = config["Services:PrinterDiscovery:BaseUrl"]
            ?? config["PRINTER_DISCOVERY_URL"]
            ?? "http://printer-discovery:5247";
    }

    public async Task<DiscoveryStreamResponse> StartDiscoveryStreamAsync(
        IReadOnlyList<PrinterBackend>? backends = null,
        bool autoRegister = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"[DISCOVERY] Forwarding discovery request to printer-discovery service at {_discoveryServiceUrl} (autoRegister={autoRegister})");

        try
        {
            // Get network discovery settings from database
            NetworkDiscoverySettings settings = _settingsService.Get<NetworkDiscoverySettings>() ?? new NetworkDiscoverySettings();

            _logger.LogInformation($"[DISCOVERY] Using settings - Subnets: {string.Join(", ", settings.DiscoverySubnets)}, Timeout: {settings.ClientTimeoutMs}ms, MaxConcurrent: {settings.MaxConcurrentRequests}");

            // Forward to the printer-discovery microservice's streaming endpoint
            HttpClient client = _httpClientFactory.CreateClient("PrinterDiscovery");
            client.BaseAddress = new Uri(_discoveryServiceUrl);
            client.Timeout = TimeSpan.FromSeconds(10);

            // Build request with settings from database
            var request = new
            {
                backends,
                autoRegister,
                subnets = settings.DiscoverySubnets.ToArray(),
                probeTimeoutMs = settings.ClientTimeoutMs,
                maxConcurrentProbes = settings.MaxConcurrentRequests
            };

            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/discovery/stream",
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                // Parse the response to get the session ID
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonDocument doc = JsonDocument.Parse(content);

                string sessionId = doc.RootElement.GetProperty("sessionId").GetString() ?? Guid.NewGuid().ToString("N");
                string message = doc.RootElement.TryGetProperty("message", out JsonElement msgElem)
                    ? msgElem.GetString() ?? "Discovery started"
                    : "Discovery started";

                _logger.LogInformation($"[DISCOVERY] Streaming discovery started with sessionId={sessionId}");

                // Cache initial progress so clients can see it when they join
                DiscoveryProgressDto initialProgress = new(
                    SessionId: sessionId,
                    CurrentNetwork: "Starting...",
                    CurrentIp: string.Empty,
                    TotalIps: 0,
                    ScannedIps: 0,
                    PrintersFound: 0,
                    PrintersExcluded: 0,
                    ProgressPercentage: 0,
                    Status: DiscoveryStatus.Starting,
                    Message: "Discovery starting...");
                _progressCache.Set(sessionId, initialProgress);

                return new DiscoveryStreamResponse(sessionId, message);
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning($"[DISCOVERY] Microservice returned {response.StatusCode}: {errorContent}");
                throw new HttpRequestException($"Discovery service returned {response.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, $"[DISCOVERY] Failed to reach printer-discovery service at {_discoveryServiceUrl}");
            throw new InvalidOperationException("Discovery service is not available. Please ensure the printer-discovery container is running.", ex);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning("[DISCOVERY] Request to printer-discovery service timed out");
            throw new InvalidOperationException("Discovery service timed out. Please try again.", ex);
        }
    }

    public async Task<DiscoveryCancelResponse> CancelDiscoveryStreamAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"[DISCOVERY] Forwarding cancel request for session {sessionId}");

        try
        {
            HttpClient client = _httpClientFactory.CreateClient("PrinterDiscovery");
            client.BaseAddress = new Uri(_discoveryServiceUrl);
            client.Timeout = TimeSpan.FromSeconds(5);

            HttpResponseMessage response = await client.PostAsync(
                $"/api/discovery/stream/{sessionId}/cancel",
                null,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"[DISCOVERY] Successfully cancelled session {sessionId}");
                return new DiscoveryCancelResponse("Discovery session cancelled");
            }
            else
            {
                _logger.LogWarning($"[DISCOVERY] Cancel request returned {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"[DISCOVERY] Failed to forward cancel request for session {sessionId}");
        }

        // Even if we can't reach the service, update local cache
        DiscoveryProgressDto cancelledProgress = new(
            SessionId: sessionId,
            CurrentNetwork: "Cancelled",
            CurrentIp: string.Empty,
            TotalIps: 0,
            ScannedIps: 0,
            PrintersFound: 0,
            PrintersExcluded: 0,
            ProgressPercentage: 0,
            Status: DiscoveryStatus.Cancelled,
            Message: "Discovery cancelled by user");

        _progressCache.Set(sessionId, cancelledProgress);

        await _hubContext.Clients.Group($"discovery-{sessionId}")
            .SendAsync("discoveryprogress", cancelledProgress, cancellationToken);

        await _hubContext.Clients.Group($"discovery-{sessionId}")
            .SendAsync("discoverycompleted", new DiscoveryCompletedDto(
                SessionId: sessionId,
                TotalPrintersFound: 0,
                TotalPrintersExcluded: 0,
                Duration: TimeSpan.Zero,
                WasCancelled: true), cancellationToken);

        return new DiscoveryCancelResponse("Discovery session cancelled");
    }
}
