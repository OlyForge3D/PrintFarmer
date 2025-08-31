using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services;

public interface INetworkDiscoveryService
{
    Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default);
}

public class NetworkDiscoveryService(
    MoonrakerClient moonrakerClient,
    PrusaLinkClient prusaLinkClient,
    INetworkDiscoverySettingsService settingsService,
    ILogger<NetworkDiscoveryService> logger) : INetworkDiscoveryService
{
    public async Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting printer network discovery...");

        var settings = settingsService.GetSettings();
        logger.LogInformation("Discovery settings: Networks={Networks}, Timeout={TimeoutMs}ms, MaxScans={MaxScans}, Ports={Ports}",
            string.Join(",", settings.NetworkRanges), settings.TimeoutMs, settings.MaxConcurrentScans, string.Join(",", settings.Ports));

        var discovered = new List<DiscoveredPrinterDto>();

        foreach (var network in settings.NetworkRanges)
        {
            logger.LogInformation("Scanning network: {Network}", network);
            var networkPrinters = await ScanNetworkAsync(network, settings, cancellationToken);
            logger.LogInformation("Network {Network} scan completed. Found {Count} printers", network, networkPrinters.Count);
            discovered.AddRange(networkPrinters);
        }

        logger.LogInformation("Network discovery completed. Found {Count} printers", discovered.Count);
        return discovered.OrderBy(p => p.IpAddress).ToList();
    }

    private async Task<List<DiscoveredPrinterDto>> ScanNetworkAsync(string network, NetworkDiscoverySettingsDto settings, CancellationToken cancellationToken)
    {
        var discovered = new List<DiscoveredPrinterDto>();

        try
        {
            var (networkAddr, cidr) = ParseCidr(network);
            var hosts = GetHostsInRange(networkAddr, cidr);
            logger.LogInformation("Network {Network} contains {HostCount} hosts to scan", network, hosts.Count);

            using var semaphore = new SemaphoreSlim(settings.MaxConcurrentScans, settings.MaxConcurrentScans);
            var tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    logger.LogDebug("Scanning host: {Host}", host);
                    var result = await ScanHostAsync(host, settings, cancellationToken);
                    if (result != null)
                    {
                        logger.LogInformation("Found printer at {Host}:{Port} - {Name} ({Backend})",
                            result.IpAddress, result.Port, result.Name, result.Backend);
                    }
                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            discovered.AddRange(results.Where(r => r != null)!);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan network: {Network}", network);
        }

        return discovered;
    }

    private static (IPAddress network, int cidr) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        var network = IPAddress.Parse(parts[0]);
        var prefixLength = int.Parse(parts[1]);
        return (network, prefixLength);
    }

    private static List<string> GetHostsInRange(IPAddress network, int cidr)
    {
        var hosts = new List<string>();

        try
        {
            var networkBytes = network.GetAddressBytes();
            var hostBits = 32 - cidr;
            var hostCount = Math.Min((int)Math.Pow(2, hostBits) - 2, 254); // Limit to reasonable size

            for (int i = 1; i <= hostCount; i++)
            {
                var hostBytes = (byte[])networkBytes.Clone();

                // For /24 networks, just increment the last octet
                if (cidr == 24)
                {
                    hostBytes[3] = (byte)i;
                }
                else
                {
                    // For other CIDR ranges, implement more complex logic if needed
                    hostBytes[3] = (byte)i;
                }

                hosts.Add(new IPAddress(hostBytes).ToString());
            }
        }
        catch (Exception)
        {
            // If range calculation fails, return empty list
        }

        return hosts;
    }

    private async Task<DiscoveredPrinterDto?> ScanHostAsync(string ipAddress, NetworkDiscoverySettingsDto settings, CancellationToken cancellationToken)
    {
        try
        {
            // Try configured ports in order
            foreach (var port in settings.Ports)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var discovered = await TryDiscoverPrinterAsync(ipAddress, port, settings.TimeoutMs, cancellationToken);
                if (discovered != null)
                {
                    return discovered;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to scan host {Host}", ipAddress);
        }

        return null;
    }

    private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(string ipAddress, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        var baseUrl = $"http://{ipAddress}:{port}";

        try
        {
            logger.LogDebug("Attempting discovery at {BaseUrl}", baseUrl);
            // Test Moonraker first (port 7125) or PrusaLink (port 80)
            if (port == 7125)
            {
                logger.LogInformation("Testing Moonraker at {BaseUrl}", baseUrl);
                var moonrakerInfo = await TryGetMoonrakerInfoAsync(baseUrl, timeoutMs, cancellationToken);
                if (moonrakerInfo != null)
                {
                    logger.LogInformation("Successfully discovered Moonraker printer at {BaseUrl}", baseUrl);
                    return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.Moonraker, moonrakerInfo);
                }
                else
                {
                    logger.LogDebug("No Moonraker response from {BaseUrl}", baseUrl);
                }
            }
            else if (port == 80)
            {
                logger.LogInformation("Testing PrusaLink at {BaseUrl}", baseUrl);
                var prusaInfo = await TryGetPrusaLinkInfoAsync(baseUrl, timeoutMs, cancellationToken);
                if (prusaInfo != null)
                {
                    logger.LogInformation("Successfully discovered PrusaLink printer at {BaseUrl}", baseUrl);
                    return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.PrusaLink, prusaInfo);
                }
                else
                {
                    logger.LogDebug("No PrusaLink response from {BaseUrl}", baseUrl);

                    // Also test if this might be a Moonraker on port 80
                    logger.LogInformation("Testing Moonraker on port 80 at {BaseUrl}", baseUrl);
                    var moonrakerInfo = await TryGetMoonrakerInfoAsync(baseUrl, timeoutMs, cancellationToken);
                    if (moonrakerInfo != null)
                    {
                        logger.LogInformation("Successfully discovered Moonraker printer on port 80 at {BaseUrl}", baseUrl);
                        return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.Moonraker, moonrakerInfo);
                    }
                    else
                    {
                        logger.LogDebug("No Moonraker response on port 80 from {BaseUrl}", baseUrl);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to discover printer at {BaseUrl}", baseUrl);
        }

        return null;
    }

    private async Task<PrinterInfo?> TryGetMoonrakerInfoAsync(string baseUrl, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            // Try to get printer info from Moonraker to get hostname and check if it's online
            var printerInfo = await moonrakerClient.GetPrinterInfoAsync(baseUrl, cts.Token);
            if (printerInfo != null && !string.IsNullOrEmpty(printerInfo.State))
            {
                return new PrinterInfo
                {
                    Name = !string.IsNullOrEmpty(printerInfo.Hostname) ? printerInfo.Hostname : ExtractHostnameFromUrl(baseUrl),
                    Manufacturer = "Unknown",
                    Model = "Klipper Printer",
                    Firmware = "Klipper/Moonraker",
                    Version = printerInfo.SoftwareVersion ?? printerInfo.State ?? "Connected"
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Moonraker discovery failed for {BaseUrl}", baseUrl);
        }

        return null;
    }

    private async Task<PrinterInfo?> TryGetPrusaLinkInfoAsync(string baseUrl, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            // Try to get info from PrusaLink API
            var info = await prusaLinkClient.ApiClient.GetInfoAsync(baseUrl, null, cts.Token);
            if (info != null)
            {
                return new PrinterInfo
                {
                    Name = info.Hostname ?? info.Name ?? ExtractHostnameFromUrl(baseUrl),
                    Manufacturer = "Prusa Research",
                    Model = info.Name ?? "Unknown Prusa",
                    Firmware = "PrusaLink",
                    Version = info.Serial
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "PrusaLink discovery failed for {BaseUrl}", baseUrl);
        }

        return null;
    }

    private static DiscoveredPrinterDto CreateDiscoveredPrinter(string ipAddress, int port, PrinterBackend backend, PrinterInfo info)
    {
        // For Moonraker printers on port 80, omit the port number from the URL for cleaner URLs
        var serverUrl = backend == PrinterBackend.Moonraker && port == 80
            ? $"http://{ipAddress}"
            : $"http://{ipAddress}:{port}";

        // Filter out "Unknown" values for manufacturer and model
        var manufacturer = IsUnknownValue(info.Manufacturer) ? null : info.Manufacturer;
        var model = IsUnknownValue(info.Model) ? null : info.Model;

        return new DiscoveredPrinterDto
        {
            IpAddress = ipAddress,
            Port = port,
            ServerUrl = serverUrl,
            Backend = backend,
            Name = info.Name ?? $"Printer-{ipAddress}",
            Manufacturer = manufacturer,
            Model = model,
            Firmware = info.Firmware,
            Version = info.Version,
            IsReachable = true,
            DiscoveredAt = DateTime.UtcNow
        };
    }

    private static bool IsUnknownValue(string? value)
    {
        return !string.IsNullOrEmpty(value) &&
               value.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractHostnameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return "Unknown";
        }
    }

    private class PrinterInfo
    {
        public string? Name { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Firmware { get; set; }
        public string? Version { get; set; }
    }
}
