using PrinterDiscovery.Services;

namespace PrinterDiscovery.BackgroundServices;

/// <summary>
/// Background service that runs periodic printer discovery
/// Can be enabled/disabled via configuration
/// </summary>
public class PeriodicDiscoveryBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PeriodicDiscoveryBackgroundService> _logger;
    private readonly bool _enabled;
    private readonly int _scanIntervalSeconds;

    public PeriodicDiscoveryBackgroundService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PeriodicDiscoveryBackgroundService> logger,
        IConfiguration config)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = config.GetValue<bool>("Discovery:EnablePeriodicDiscovery", true);
        _scanIntervalSeconds = config.GetValue<int>("Discovery:ScanIntervalSeconds", 300);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Periodic discovery is disabled. Use manual scan endpoint instead");
            return;
        }

        _logger.LogInformation("Periodic discovery background service starting (interval: {Interval}s)", _scanIntervalSeconds);

        try
        {
            // Create a scope for the scoped service
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            INetworkDiscoveryService discoveryService = scope.ServiceProvider.GetRequiredService<INetworkDiscoveryService>();

            await discoveryService.StartPeriodicDiscoveryAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Periodic discovery stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic discovery background service failed");
        }
    }
}
