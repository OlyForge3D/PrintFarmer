using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;

namespace PrinterDiscovery.Services;

/// <summary>
/// Service that discovers printers using the shared discovery probes and registers them with the central API.
/// Supports both periodic discovery (push mode) and manual triggers (pull mode).
/// </summary>
public interface INetworkDiscoveryService
{
    /// <summary>
    /// Perform a single discovery scan (manual/pull mode) using locally configured subnets.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<IReadOnlyList<DiscoveredPrinterDto>> ScanOnceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform a single discovery scan using the specified subnets from API settings.
    /// </summary>
    /// <param name="subnets">CIDR subnets to scan.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<IReadOnlyList<DiscoveredPrinterDto>> ScanOnceAsync(IList<string> subnets, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register discovered printers with the central API
    /// </summary>
    /// <param name="printers">List of discovered printers to register.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task RegisterPrintersAsync(IReadOnlyList<DiscoveredPrinterDto> printers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start periodic discovery (push mode - runs as background service)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop periodic discovery.</param>
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

        _scanIntervalSeconds = _config.GetValue("Discovery:ScanIntervalSeconds", 300); // 5 minutes default
        _probeTimeoutMs = _config.GetValue("Discovery:ProbeTimeoutMs", 200); // 200ms per probe
    }

    /// <summary>
    /// Manual discovery scan (pull mode)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the scan.</param>
    public Task<IReadOnlyList<DiscoveredPrinterDto>> ScanOnceAsync(CancellationToken cancellationToken = default)
    {
        // Fallback: use locally configured subnets from env vars
        string subnetsConfig = _config["Discovery:Subnets"] ?? "10.0.0.0/24";
        string[] subnets = subnetsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return ScanOnceAsync(subnets, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveredPrinterDto>> ScanOnceAsync(IList<string> subnets, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting printer discovery scan...");

            List<string> ipAddresses = GenerateIpAddresses(subnets.ToList());

            _logger.LogInformation("Scanning {IpCount} IP addresses across {SubnetCount} subnets", ipAddresses.Count, subnets.Count);

            // Use the core discovery service to probe all IPs
            int maxConcurrent = _config.GetValue("Discovery:MaxConcurrentProbes", 50);
            List<DiscoveredPrinterDto> discovered = await _coreDiscovery.DiscoverMultipleAsync(
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
    /// <param name="printers">List of discovered printers to register.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task RegisterPrintersAsync(IReadOnlyList<DiscoveredPrinterDto> printers, CancellationToken cancellationToken = default)
    {
        foreach (DiscoveredPrinterDto printer in printers)
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
    /// <param name="cancellationToken">Cancellation token to stop periodic discovery.</param>
    public async Task StartPeriodicDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting periodic discovery with {Interval}s interval", _scanIntervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Scan for printers
                IReadOnlyList<DiscoveredPrinterDto> discovered = await ScanOnceAsync(cancellationToken);

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
    /// <param name="subnets">List of CIDR subnet strings (e.g., "192.168.1.0/24").</param>
    private static List<string> GenerateIpAddresses(List<string> subnets)
    {
        List<string> ips = new List<string>();

        foreach (string subnet in subnets)
        {
            try
            {
                string[] parts = subnet.Trim().Split('/');
                if (parts.Length != 2)
                {
                    continue;
                }

                SubnetParser network = SubnetParser.Parse(subnet.Trim());

                // Limit to first 254 addresses to avoid excessive scanning
                int addressCount = (int)Math.Min(254, network.Total);
                for (int i = 1; i < addressCount; i++)
                {
                    IPAddress ipAddr = network.AddToFirstUsable(i);
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
    /// <param name="printer">The discovered printer to register.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task RegisterDiscoveredPrinterAsync(DiscoveredPrinterDto printer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the list of server URLs for printers already registered in the system.
    /// Used to filter out already-known printers during discovery.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<HashSet<string>> GetRegisteredPrinterUrlsAsync(CancellationToken cancellationToken = default);
}

public class ApiClient(HttpClient httpClient, ILogger<ApiClient> logger) : IApiClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger<ApiClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task RegisterDiscoveredPrinterAsync(DiscoveredPrinterDto printer, CancellationToken cancellationToken = default)
    {
        try
        {
            // Send discovered printer directly to API (single object, not array)
            string json = JsonSerializer.Serialize(printer);
            using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient.PostAsync(new Uri("/api/printers/discovered", UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();

            _logger.LogDebug("Successfully registered printer with API");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register printer with API");
            throw;
        }
    }

    public async Task<HashSet<string>> GetRegisteredPrinterUrlsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(new Uri("/api/printers", UriKind.Relative), cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get registered printers: {StatusCode}", response.StatusCode);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Parse JSON array and extract serverUrl from each printer
            using JsonDocument doc = JsonDocument.Parse(json);
            HashSet<string> urls = new(StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement printer in doc.RootElement.EnumerateArray())
            {
                if (printer.TryGetProperty("serverUrl", out JsonElement urlElement))
                {
                    string? url = urlElement.GetString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        urls.Add(url);
                    }
                }
            }

            _logger.LogDebug("Found {Count} registered printers", urls.Count);
            return urls;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get registered printers from API");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// Simple CIDR subnet parser
/// </summary>
public sealed class SubnetParser
{
    public IPAddress FirstUsable { get; set; } = IPAddress.Parse("0.0.0.0");

    public long Total { get; set; }

    public static SubnetParser Parse(string cidr)
    {
        ArgumentNullException.ThrowIfNull(cidr);
        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            throw new ArgumentException("Invalid CIDR format", nameof(cidr));
        }

        IPAddress ip = IPAddress.Parse(parts[0]);
        int prefixLength = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

        int hostBits = 32 - prefixLength;
        long total = (long)Math.Pow(2, hostBits);

        return new SubnetParser
        {
            FirstUsable = ip,
            Total = total
        };
    }

    public IPAddress AddToFirstUsable(int offset)
    {
        byte[] bytes = FirstUsable.GetAddressBytes();
        uint value = BitConverter.ToUInt32([bytes[3], bytes[2], bytes[1], bytes[0]], 0);
        value += (uint)offset;
        byte[] newBytes = BitConverter.GetBytes(value);
        return new IPAddress([newBytes[3], newBytes[2], newBytes[1], newBytes[0]]);
    }
}
