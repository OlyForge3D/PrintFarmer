using System.Net.Http.Json;

namespace PrinterDiscovery.BackgroundServices;

/// <summary>
/// Background service that sends periodic heartbeats to the API
/// Allows API to track if discovery service is actively running
/// Heartbeat updates the LastHeartbeat timestamp in NetworkDiscoverySettings
/// </summary>
public class HeartbeatBackgroundService : BackgroundService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HeartbeatBackgroundService> _logger;
    private readonly string _apiBaseUrl;
    private readonly int _heartbeatIntervalSeconds;

    public HeartbeatBackgroundService(
        HttpClient httpClient,
        ILogger<HeartbeatBackgroundService> logger,
        IConfiguration config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiBaseUrl = config["Discovery:ApiBaseUrl"] ?? "http://api:5245";
        _heartbeatIntervalSeconds = config.GetValue("Discovery:HeartbeatIntervalSeconds", 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat background service starting (interval: {Interval}s)", _heartbeatIntervalSeconds);

        // Wait a bit before first heartbeat to allow system to stabilize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Send heartbeat to API
                    string heartbeatUrl = $"{_apiBaseUrl}/api/settings/NetworkDiscovery/heartbeat";

                    _logger.LogDebug("Sending heartbeat to {Url}", heartbeatUrl);

                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout for heartbeat

                    using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                        heartbeatUrl,
                        new { timestamp = DateTime.UtcNow },
                        cts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Heartbeat sent successfully");
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Heartbeat failed with status {StatusCode}: {ReasonPhrase}",
                            response.StatusCode,
                            response.ReasonPhrase);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Heartbeat request timed out");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Failed to send heartbeat - API may be unreachable");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while sending heartbeat");
                }

                // Wait for next heartbeat interval
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_heartbeatIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Heartbeat background service stopped");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heartbeat background service failed");
        }
    }
}
