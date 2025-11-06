using System.Collections.Concurrent;
using System.Net;

namespace PrinterDiscovery.Services;

/// <summary>
/// Network scanner that detects printers on the local network
/// by performing TCP port scans and probe attempts
/// </summary>
public class NetworkScanner : INetworkScanner
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NetworkScanner> _logger;
    private readonly IConfiguration _config;

    // Discovery probe endpoints and ports
    private readonly List<ProbeTarget> _probeTargets = new()
    {
        // Moonraker (Klipper)
        new ProbeTarget { Port = 7125, Path = "/printer/info", Name = "Moonraker" },

        // PrusaLink
        new ProbeTarget { Port = 8080, Path = "/api/version", Name = "PrusaLink" },
        new ProbeTarget { Port = 80, Path = "/api/version", Name = "PrusaLink (alt)" },

        // OctoPrint
        new ProbeTarget { Port = 5000, Path = "/api/version", Name = "OctoPrint" },

        // SDCP (Creality)
        new ProbeTarget { Port = 80, Path = "/api/version", Name = "SDCP" }
    };

    public NetworkScanner(
        HttpClient httpClient,
        ILogger<NetworkScanner> logger,
        IConfiguration config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Scan the network for printers by probing known IPs and ports
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPrinter>> ScanNetworkAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting network scan for printers...");

            // Get list of subnets to scan from config
            var subnetsConfig = _config["Discovery:Subnets"] ?? "192.168.0.0/16,10.0.0.0/8";
            var subnets = subnetsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries);

            var discovered = new ConcurrentBag<DiscoveredPrinter>();
            var ipAddresses = GenerateIpAddresses(subnets);

            _logger.LogInformation(
                "Scanning {IpCount} IP addresses across {SubnetCount} subnets",
                ipAddresses.Count,
                subnets.Length);

            // Probe each IP address concurrently (with throttling)
            var probeTimeout = _config.GetValue<int>("Discovery:ProbeTimeoutMs", 1000);
            var maxConcurrent = _config.GetValue<int>("Discovery:MaxConcurrentProbes", 50);

            using var semaphore = new SemaphoreSlim(maxConcurrent);
            var tasks = ipAddresses.Select(async ip =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var printer = await ProbeIpAsync(ip, probeTimeout, cancellationToken);
                    if (printer != null)
                    {
                        discovered.Add(printer);
                        _logger.LogInformation("Found printer: {Printer}", printer);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            _logger.LogInformation("Network scan completed. Found {Count} printers", discovered.Count);
            return discovered.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Network scan failed");
            throw;
        }
    }

    /// <summary>
    /// Probe a single IP address to see if a printer is running
    /// </summary>
    private async Task<DiscoveredPrinter?> ProbeIpAsync(
        string ipAddress,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        // Try each probe target
        foreach (var target in _probeTargets)
        {
            try
            {
                var url = $"http://{ipAddress}:{target.Port}{target.Path}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                var response = await _httpClient.GetAsync(url, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    // Try to get hostname
                    var hostname = await ResolveHostnameAsync(ipAddress, cancellationToken) ?? ipAddress;

                    return new DiscoveredPrinter
                    {
                        Hostname = hostname,
                        IpAddress = ipAddress,
                        Port = target.Port,
                        PrinterBackend = MapProbeNameToBackend(target.Name),
                        FriendlyName = hostname,
                        DiscoveredAt = DateTime.UtcNow
                    };
                }
            }
            catch (TaskCanceledException)
            {
                // Timeout - expected for non-existent hosts
            }
            catch (HttpRequestException)
            {
                // Connection refused - expected for closed ports
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Probe failed for {Ip}:{Port}", ipAddress, target.Port);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve IP address to hostname via reverse DNS
    /// </summary>
    private async Task<string?> ResolveHostnameAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ipAddress, cancellationToken);
            return entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generate all IP addresses from a list of CIDR subnets
    /// </summary>
    private static List<string> GenerateIpAddresses(string[] subnets)
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

                var network = IPNetwork.Parse(subnet.Trim());
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

    /// <summary>
    /// Map probe name to printer backend type
    /// </summary>
    private static string MapProbeNameToBackend(string probeName)
    {
        return probeName switch
        {
            "Moonraker" => "moonraker",
            "PrusaLink" or "PrusaLink (alt)" => "prusalink",
            "OctoPrint" => "octoprint",
            "SDCP" => "sdcp",
            _ => "unknown"
        };
    }

    private class ProbeTarget
    {
        public int Port { get; set; }

        public string Path { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }
}

/// <summary>
/// Simple CIDR subnet parser
/// </summary>
public class IPNetwork
{
    public IPAddress FirstUsable { get; set; } = IPAddress.Parse("0.0.0.0");

    public long Total { get; set; }

    public static IPNetwork Parse(string cidr)
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

        return new IPNetwork
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
