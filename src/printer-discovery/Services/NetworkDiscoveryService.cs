namespace PrinterDiscovery.Services;

/// <summary>
/// Represents a printer discovered on the network
/// </summary>
public record DiscoveredPrinter
{
    public string Hostname { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 80;
    public string PrinterBackend { get; init; } = "unknown"; // moonraker, prusalink, octoprint, sdcp
    public string? FriendlyName { get; init; }
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;

    public override string ToString() => $"{FriendlyName ?? Hostname} ({IpAddress}:{Port})";
}

/// <summary>
/// Service that discovers printers and registers them with the central API
/// Supports both periodic discovery (push mode) and manual triggers (pull mode)
/// </summary>
public interface INetworkDiscoveryService
{
    /// <summary>
    /// Perform a single discovery scan (manual/pull mode)
    /// </summary>
    Task<IReadOnlyList<DiscoveredPrinter>> ScanOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register discovered printers with the central API
    /// </summary>
    Task RegisterPrintersAsync(IReadOnlyList<DiscoveredPrinter> printers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start periodic discovery (push mode - runs as background service)
    /// </summary>
    Task StartPeriodicDiscoveryAsync(CancellationToken cancellationToken = default);
}

public class NetworkDiscoveryService : INetworkDiscoveryService
{
    private readonly INetworkScanner _scanner;
    private readonly IApiClient _apiClient;
    private readonly ILogger<NetworkDiscoveryService> _logger;
    private readonly int _scanIntervalSeconds;

    public NetworkDiscoveryService(
        INetworkScanner scanner,
        IApiClient apiClient,
        ILogger<NetworkDiscoveryService> logger,
        IConfiguration config)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _scanIntervalSeconds = config.GetValue<int>("Discovery:ScanIntervalSeconds", 300); // 5 minutes default
    }

    /// <summary>
    /// Manual discovery scan (pull mode)
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPrinter>> ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting manual printer discovery scan...");
            var discovered = await _scanner.ScanNetworkAsync(cancellationToken);
            _logger.LogInformation("Discovery scan found {Count} printers", discovered.Count);
            return discovered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery scan failed");
            throw;
        }
    }

    /// <summary>
    /// Register discovered printers with central API
    /// </summary>
    public async Task RegisterPrintersAsync(IReadOnlyList<DiscoveredPrinter> printers, CancellationToken cancellationToken = default)
    {
        foreach (var printer in printers)
        {
            try
            {
                _logger.LogInformation("Registering discovered printer: {Printer}", printer);

                var dto = new RegisterDiscoveredPrinterDto
                {
                    Hostname = printer.Hostname,
                    IpAddress = printer.IpAddress,
                    Port = printer.Port,
                    PrinterBackend = printer.PrinterBackend,
                    FriendlyName = printer.FriendlyName,
                    DiscoveredAt = printer.DiscoveredAt
                };

                await _apiClient.RegisterDiscoveredPrinterAsync(dto, cancellationToken);
                _logger.LogInformation("Successfully registered printer: {Printer}", printer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register printer: {Printer}", printer);
            }
        }
    }

    /// <summary>
    /// Start periodic discovery (push mode - runs as background service)
    /// </summary>
    public async Task StartPeriodicDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting periodic discovery with {Interval}s interval", _scanIntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Scan for printers
                var discovered = await ScanOnceAsync(cancellationToken);

                // Register them
                await RegisterPrintersAsync(discovered, cancellationToken);

                // Wait before next scan
                await Task.Delay(TimeSpan.FromSeconds(_scanIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Periodic discovery stopped");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in periodic discovery loop");
                // Continue scanning despite errors
                await Task.Delay(TimeSpan.FromSeconds(_scanIntervalSeconds), cancellationToken);
            }
        }
    }
}

/// <summary>
/// Network scanner that uses discovery probes to detect printers
/// </summary>
public interface INetworkScanner
{
    /// <summary>
    /// Scan the network for printers using available probes
    /// </summary>
    Task<IReadOnlyList<DiscoveredPrinter>> ScanNetworkAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO for registering discovered printers with the API
/// </summary>
public class RegisterDiscoveredPrinterDto
{
    public string Hostname { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public string PrinterBackend { get; set; } = "unknown";
    public string? FriendlyName { get; set; }
    public DateTime DiscoveredAt { get; set; }
}

/// <summary>
/// API client for registering printers with the central service
/// </summary>
public interface IApiClient
{
    Task RegisterDiscoveredPrinterAsync(RegisterDiscoveredPrinterDto dto, CancellationToken cancellationToken = default);
}

public class ApiClient : IApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ApiClient> _logger;

    public ApiClient(HttpClient httpClient, ILogger<ApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RegisterDiscoveredPrinterAsync(RegisterDiscoveredPrinterDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(dto);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/printers/discovered", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            _logger.LogDebug("Successfully registered printer with API");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register printer with API");
            throw;
        }
    }
}
