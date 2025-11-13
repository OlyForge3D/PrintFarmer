using Farm.Shared.Discovery;
using Farm.Web.Shared;
using System.Net;
using System.Net.NetworkInformation;

namespace PrinterDiscovery.Services;

/// <summary>
/// Service that discovers printers using the shared discovery probes and registers them with the central API.
/// Supports both periodic discovery (push mode) and manual triggers (pull mode).
/// </summary>
public interface INetworkDiscoveryService
{
    /// <summary>
    /// Perform a single discovery scan (manual/pull mode)
    /// </summary>
    Task<IReadOnlyList<DiscoveredPrinterDto>> ScanOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register discovered printers with the central API
    /// </summary>
    Task RegisterPrintersAsync(IReadOnlyList<DiscoveredPrinterDto> printers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start periodic discovery (push mode - runs as background service)
    /// </summary>
    Task StartPeriodicDiscoveryAsync(CancellationToken cancellationToken = default);
}

public class NetworkDiscoveryService : INetworkDiscoveryService
{
    private readonly ICoreNetworkDiscoveryService _coreDiscovery;
    private readonly IApiClient _apiClient;
    private readonly ILogger<NetworkDiscoveryService> _logger;
    private readonly IConfiguration _config;
    private readonly int _scanIntervalSeconds;
    private readonly int _probeTimeoutMs;

    public NetworkDiscoveryService(
        ICoreNetworkDiscoveryService coreDiscovery,
        IApiClient apiClient,
        ILogger<NetworkDiscoveryService> logger,
        IConfiguration config)
    {
        _coreDiscovery = coreDiscovery ?? throw new ArgumentNullException(nameof(coreDiscovery));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _scanIntervalSeconds = _config.GetValue<int>("Discovery:ScanIntervalSeconds", 300); // 5 minutes default
        _probeTimeoutMs = _config.GetValue<int>("Discovery:ProbeTimeoutMs", 200); // 200ms per probe
    }

    /// <summary>
    /// Manual discovery scan (pull mode)
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPrinterDto>> ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting manual printer discovery scan...");
            
            // Get configured network ranges or auto-detect
            var subnetsConfig = _config["Discovery:Subnets"] ?? "192.168.0.0/16,10.0.0.0/8";
            var subnets = subnetsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var ipAddresses = GenerateIpAddresses(subnets.ToList());
            
            _logger.LogInformation("Scanning {IpCount} IP addresses across {SubnetCount} subnets", ipAddresses.Count, subnets.Length);

            // Use the core discovery service to probe all IPs
            var maxConcurrent = _config.GetValue<int>("Discovery:MaxConcurrentProbes", 50);
            var discovered = await _coreDiscovery.DiscoverMultipleAsync(
                ipAddresses,
                _probeTimeoutMs,
                maxConcurrent,
                backendFilter: null,
                cancellationToken);

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
    public async Task RegisterPrintersAsync(IReadOnlyList<DiscoveredPrinterDto> printers, CancellationToken cancellationToken = default)
    {
        foreach (var printer in printers)
        {
            try
            {
                _logger.LogInformation("Registering discovered printer: {Printer} ({IpAddress})", printer.Name, printer.IpAddress);

                // Send discovered printer directly to API (no intermediate bridge DTO)
                await _apiClient.RegisterDiscoveredPrinterAsync(printer, cancellationToken);
                _logger.LogInformation("Successfully registered printer: {Printer}", printer.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register printer: {Printer}", printer.Name);
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

    /// <summary>
    /// Generate all IP addresses from a list of CIDR subnets
    /// </summary>
    private static List<string> GenerateIpAddresses(List<string> subnets)
    {
        var ips = new List<string>();

        foreach (var subnet in subnets)
        {
            try
            {
                var parts = subnet.Trim().Split('/');
                if (parts.Length != 2)
                {
                    continue;
                }

                var network = SubnetParser.Parse(subnet.Trim());
                // Limit to first 254 addresses to avoid excessive scanning
                var addressCount = (int)Math.Min(254, network.Total);
                for (int i = 1; i < addressCount; i++)
                {
                    var ipAddr = network.AddToFirstUsable(i);
                    ips.Add(ipAddr.ToString());
                }
            }
            catch
            {
                // Invalid subnet format, skip
            }
        }

        return ips;
    }
}

/// <summary>
/// API client for registering discovered printers with the central service.
/// Sends printers discovered by the local network scan to the central API for persistence.
/// </summary>
public interface IApiClient
{
    /// <summary>
    /// Register a discovered printer with the central API.
    /// The API will deduplicate and persist the printer in the database.
    /// </summary>
    Task RegisterDiscoveredPrinterAsync(DiscoveredPrinterDto printer, CancellationToken cancellationToken = default);
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

    public async Task RegisterDiscoveredPrinterAsync(DiscoveredPrinterDto printer, CancellationToken cancellationToken = default)
    {
        try
        {
            // Send discovered printer directly to API (single object, not array)
            var json = System.Text.Json.JsonSerializer.Serialize(printer);
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

/// <summary>
/// Simple CIDR subnet parser
/// </summary>
public class SubnetParser
{
    public IPAddress FirstUsable { get; set; } = IPAddress.Parse("0.0.0.0");

    public long Total { get; set; }

    public static SubnetParser Parse(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            throw new ArgumentException("Invalid CIDR format");
        }

        var ip = IPAddress.Parse(parts[0]);
        var prefixLength = int.Parse(parts[1]);

        var ipBytes = ip.GetAddressBytes();
        var hostBits = 32 - prefixLength;
        var total = (long)Math.Pow(2, hostBits);

        return new SubnetParser
        {
            FirstUsable = ip,
            Total = total
        };
    }

    public IPAddress AddToFirstUsable(int offset)
    {
        var bytes = FirstUsable.GetAddressBytes();
        var value = BitConverter.ToUInt32(new[] { bytes[3], bytes[2], bytes[1], bytes[0] }, 0);
        value += (uint)offset;
        var newBytes = BitConverter.GetBytes(value);
        return new IPAddress(new[] { newBytes[3], newBytes[2], newBytes[1], newBytes[0] });
    }
}
