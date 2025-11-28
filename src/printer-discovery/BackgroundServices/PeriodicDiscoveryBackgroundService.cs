using System.Text.Json;
using Farm.Infrastructure.Settings;
using PrinterDiscovery.Services;

namespace PrinterDiscovery.BackgroundServices;

/// <summary>
/// Background service that runs periodic printer discovery.
/// Fetches settings from the API to determine if enabled and scan interval.
/// </summary>
public class PeriodicDiscoveryBackgroundService : BackgroundService, IDisposable
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PeriodicDiscoveryBackgroundService> _logger;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _settingsRefreshInterval = TimeSpan.FromMinutes(1);
    private bool _disposed;

    public PeriodicDiscoveryBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PeriodicDiscoveryBackgroundService> logger,
        IConfiguration config,
        IHttpClientFactory httpClientFactory)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        string apiBaseUrl = config["Discovery:ApiBaseUrl"] ?? "http://api:5245";
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri(apiBaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Periodic discovery background service starting");

        // Wait for API to be ready
        await WaitForApiAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Fetch current settings from API
                NetworkDiscoverySettings? settings = await FetchSettingsAsync(stoppingToken);
                
                if (settings == null || !settings.BackgroundScanEnabled)
                {
                    _logger.LogDebug("Background scanning is disabled. Checking again in {Interval}", _settingsRefreshInterval);
                    await Task.Delay(_settingsRefreshInterval, stoppingToken);
                    continue;
                }

                TimeSpan scanInterval = TimeSpan.FromMinutes(settings.BackgroundScanIntervalMinutes);
                _logger.LogInformation("Running periodic discovery scan (interval: {Interval} minutes)", settings.BackgroundScanIntervalMinutes);

                // Run a discovery scan
                await RunDiscoveryScanAsync(stoppingToken);

                // Wait for the configured interval
                _logger.LogDebug("Next scan in {Interval} minutes", settings.BackgroundScanIntervalMinutes);
                await Task.Delay(scanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic discovery loop, retrying in 1 minute");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("Periodic discovery background service stopped");
    }

    private async Task WaitForApiAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync("/healthz", stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("API is ready");
                    return;
                }
            }
            catch
            {
                // API not ready yet
            }

            _logger.LogDebug("Waiting for API to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<NetworkDiscoverySettings?> FetchSettingsAsync(CancellationToken stoppingToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync("/api/settings/NetworkDiscovery", stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch network discovery settings: {StatusCode}", response.StatusCode);
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(stoppingToken);
            return JsonSerializer.Deserialize<NetworkDiscoverySettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching network discovery settings");
            return null;
        }
    }

    private async Task RunDiscoveryScanAsync(CancellationToken stoppingToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            INetworkDiscoveryService discoveryService = scope.ServiceProvider.GetRequiredService<INetworkDiscoveryService>();
            
            IReadOnlyList<Farm.Infrastructure.DiscoveredPrinterDto> printers = await discoveryService.ScanOnceAsync(stoppingToken);
            _logger.LogInformation("Periodic discovery scan found {Count} printers", printers.Count);
            
            if (printers.Count > 0)
            {
                await discoveryService.RegisterPrintersAsync(printers, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during periodic discovery scan");
        }
    }
}
