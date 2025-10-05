using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services;

public interface INetworkDiscoveryService
{
    Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default);
    Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default);
    Task DiscoverPrintersWithProgressAsync(string sessionId, List<PrinterBackend>? backends, CancellationToken cancellationToken = default);
}

public partial class NetworkDiscoveryService(
    MoonrakerClient moonrakerClient,
    PrusaLinkClient prusaLinkClient,
    INetworkDiscoverySettingsService settingsService,
    IHubContext<PrinterHub> hubContext,
    IDiscoveryProgressCache progressCache,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger) : INetworkDiscoveryService
{
    private readonly MoonrakerClient _moonrakerClient = moonrakerClient;
    private readonly PrusaLinkClient _prusaLinkClient = prusaLinkClient;
    private readonly INetworkDiscoverySettingsService _settingsService = settingsService;
    private readonly IHubContext<PrinterHub> _hubContext = hubContext;
    private readonly IDiscoveryProgressCache _progressCache = progressCache;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IUnifiedLoggingService _logger = logger;

    public async Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Starting printer network discovery...", null, null);

        // Gather existing printers to exclude from results (fresh scope for safety)
        HashSet<string> existingServerUrls = LoadExistingPrinterUrlsSafe();

        NetworkDiscoverySettingsDto settings = _settingsService.GetSettings();
        // Auto-detect network ranges if none configured
        if (settings.NetworkRanges.Count == 0)
        {
            List<string> autoRanges = DetectLocalNetworks().ToList();
            if (autoRanges.Count > 0)
            {
                settings.NetworkRanges.AddRange(autoRanges);
                _logger.LogInformation($"Auto-detected {autoRanges.Count} network range(s) for discovery: {string.Join(",", autoRanges)}", null, null);
            }
            else
            {
                _logger.LogWarning($"No network ranges configured and none could be auto-detected. Discovery will return empty result.", null, null);
                return [];
            }
        }
        _logger.LogInformation($"Discovery settings: Networks={string.Join(",", settings.NetworkRanges)}, Timeout={settings.TimeoutMs}ms, MaxScans={settings.MaxConcurrentScans}, Ports={string.Join(",", settings.Ports)}", null, null);

        List<DiscoveredPrinterDto> discovered = new();

        foreach (string network in settings.NetworkRanges)
        {
            _logger.LogInformation($"Scanning network: {network}", null, null);
            List<DiscoveredPrinterDto> networkPrinters = await ScanNetworkAsync(network, settings, existingServerUrls, cancellationToken);
            _logger.LogInformation($"Network {network} scan completed. Found {networkPrinters.Count} printers", null, null);
            discovered.AddRange(networkPrinters);
        }

        _logger.LogInformation($"Network discovery completed. Found {discovered.Count} printers", null, null);
        if (discovered.Count == 0)
        {
            return new List<DiscoveredPrinterDto>();
        }

        return discovered.OrderBy(p => p.IpAddress).ToList();
    }

    public async Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await DiscoverPrintersWithProgressAsync(sessionId, null, cancellationToken);
    }

    public async Task DiscoverPrintersWithProgressAsync(string sessionId, List<PrinterBackend>? backends, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Starting printer network discovery...", null, null);

        // Gather existing printers to exclude from streaming results (fresh scope - background task may outlive original request scope)
        HashSet<string> existingServerUrls = LoadExistingPrinterUrlsSafe();

        NetworkDiscoverySettingsDto settings = _settingsService.GetSettings();
        
        // Override backends if provided in the request
        if (backends != null && backends.Count > 0)
        {
            settings = settings with { Backends = backends };
        }
        
        bool autoDetectedNetworks = false;
        // Auto-detect network ranges if none configured
        if (settings.NetworkRanges.Count == 0)
        {
            List<string> autoRanges = DetectLocalNetworks().ToList();
            if (autoRanges.Count > 0)
            {
                settings.NetworkRanges.AddRange(autoRanges);
                autoDetectedNetworks = true;
                _logger.LogInformation($"Auto-detected {autoRanges.Count} network range(s) for streaming discovery: {string.Join(",", autoRanges)}", null, null);
            }
            else
            {
                _logger.LogWarning($"No network ranges configured and none could be auto-detected. Streaming discovery will send immediate completion.", null, null);
            }
        }
        _logger.LogInformation($"Discovery settings: Networks={string.Join(",", settings.NetworkRanges)}, Timeout={settings.TimeoutMs}ms, MaxScans={settings.MaxConcurrentScans}, Ports={string.Join(",", settings.Ports)}", null, null);

        int totalIps = 0;
        int scannedIps = 0;
        int foundPrinters = 0;

        // Calculate total IPs to scan
        foreach (string network in settings.NetworkRanges)
        {
            try
            {
                (IPAddress? networkAddr, int cidr) = ParseCidr(network);
                List<string> hosts = GetHostsInRange(networkAddr, cidr);
                totalIps += hosts.Count;
            }
            catch
            {
                // Skip invalid networks
            }
        }

        // Send initial progress
        DiscoveryProgressDto initialProgress = new(
            sessionId,
            settings.NetworkRanges.FirstOrDefault() ?? string.Empty,
            string.Empty,
            totalIps,
            0,
            0,
            0,
            0d,
            DiscoveryStatus.Starting,
            null,
            settings.NetworkRanges,
            autoDetectedNetworks
        );
        _progressCache.Set(sessionId, initialProgress);
        await _hubContext.Clients
            .Group($"discovery-{sessionId}")
            .SendAsync(
                "DiscoveryProgress",
                initialProgress,
                cancellationToken);

        int excludedPrinters = 0; // count of printers skipped because already present

        _logger.LogInformation($"Starting multi-network scan for {settings.NetworkRanges.Count} networks: {string.Join(", ", settings.NetworkRanges)}", null, null);

        foreach (string network in settings.NetworkRanges)
        {
            _logger.LogInformation($"Scanning network: {network}", null, null);

            try
            {
                // Calculate hosts count for this network to properly track scanned IPs
                (IPAddress? networkAddr, int cidr) = ParseCidr(network);
                List<string> hosts = GetHostsInRange(networkAddr, cidr);

                // Pass current progress state to network scanner
                List<DiscoveredPrinterDto> networkPrinters = await ScanNetworkWithProgressAsync(network, settings, existingServerUrls, sessionId, totalIps, scannedIps, foundPrinters, autoDetectedNetworks, () => Interlocked.Increment(ref excludedPrinters), cancellationToken);

                // Update cumulative counters after network completes
                scannedIps += hosts.Count; // Track actual IPs scanned, not printers found
                foundPrinters += networkPrinters.Count;

                _logger.LogInformation($"Network {network} scan completed. Found {networkPrinters.Count} printers", null, null);
                _logger.LogInformation($"Progress after {network}: {scannedIps}/{totalIps} IPs ({((double)scannedIps / totalIps * 100):F1}%), {foundPrinters} total", null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning($"Discovery cancelled during {network} scan after {scannedIps}/{totalIps} IPs", null, null);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to scan network: {Network}", network);
                _logger.LogWarning("Failed to scan network {Network}, continuing with next network. Scanned {ScannedIps}/{TotalIps} IPs so far", network, scannedIps, totalIps);
                // Continue with next network range instead of stopping entire discovery
                continue;
            }
        }

        _logger.LogInformation($"Multi-network scan complete: {scannedIps}/{totalIps} IPs, {foundPrinters} printers", null, null);

        // Emit a final progress snapshot with Completed status for clients that only listen to progress stream
        if (totalIps > 0)
        {
            DiscoveryProgressDto finalProgress = new(
                sessionId,
                string.Empty,
                string.Empty,
                totalIps,
                totalIps,
                foundPrinters,
                excludedPrinters,
                100d,
                DiscoveryStatus.Completed,
                null,
                settings.NetworkRanges,
                autoDetectedNetworks
            );
            _progressCache.Set(sessionId, finalProgress);
            await _hubContext.Clients
                .Group($"discovery-{sessionId}")
                .SendAsync("DiscoveryProgress", finalProgress, cancellationToken);
        }

        // Send completion signal
        await _hubContext.Clients
            .Group($"discovery-{sessionId}")
            .SendAsync(
                "DiscoveryCompleted",
                new DiscoveryCompletedDto(
                    sessionId,
                    foundPrinters,
                    excludedPrinters,
                    TimeSpan.Zero, // client calculates
                    cancellationToken.IsCancellationRequested,
                    settings.NetworkRanges,
                    autoDetectedNetworks
                ),
                cancellationToken);

        // NOTE: Do not clear the cached progress immediately. Leaving the final Completed snapshot
        // allows late group joiners (e.g. tests or UI racing right after start) to still receive a
        // DiscoveryProgress event. A new discovery run will overwrite this entry anyway.
        _logger.LogInformation($"Network discovery completed. Found {foundPrinters} printers", null, null);
    }

    private static HashSet<string> DetectLocalNetworks()
    {
        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                if (ni.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IPInterfaceProperties ipProps = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        // IPv4 only
                        continue;
                    }
                    IPAddress address = ua.Address;
                    IPAddress mask = ua.IPv4Mask;
                    if (mask == null)
                    {
                        continue;
                    }
                    int cidr = MaskToCidr(mask);
                    if (cidr <= 0 || cidr > 32)
                    {
                        continue;
                    }
                    IPAddress networkAddress = GetNetworkAddress(address, mask);
                    string cidrString = $"{networkAddress}/{cidr}";
                    results.Add(cidrString);
                }
            }
        }
        catch
        {
            // Swallow exceptions - auto-detection is best-effort
        }
        return results;
    }

    private static int MaskToCidr(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int bits = 0;
        foreach (byte b in bytes)
        {
            byte value = b;
            for (int i = 0; i < 8; i++)
            {
                if ((value & 0x80) == 0x80)
                {
                    bits++;
                    value <<= 1;
                }
                else
                {
                    break;
                }
            }
        }
        return bits;
    }

    private static IPAddress GetNetworkAddress(IPAddress address, IPAddress mask)
    {
        byte[] ipBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] networkBytes = new byte[ipBytes.Length];
        for (int i = 0; i < ipBytes.Length; i++)
        {
            networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }
        return new IPAddress(networkBytes);
    }

    private async Task<List<DiscoveredPrinterDto>> ScanNetworkAsync(string network, NetworkDiscoverySettingsDto settings, HashSet<string> existingServerUrls, CancellationToken cancellationToken)
    {
        List<DiscoveredPrinterDto> discovered = new();

        try
        {
            (IPAddress? networkAddr, int cidr) = ParseCidr(network);
            List<string> hosts = GetHostsInRange(networkAddr, cidr);
            _logger.LogInformation($"Network {network} contains {hosts.Count} hosts to scan", null, null);

            using SemaphoreSlim semaphore = new(settings.MaxConcurrentScans, settings.MaxConcurrentScans);
            Task<DiscoveredPrinterDto?>[] tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogDebug($"Scanning host: {host}", null, null);
                    DiscoveredPrinterDto? result = await ScanHostAsync(host, settings, existingServerUrls, cancellationToken);
                    if (result != null && !existingServerUrls.Contains(NormalizeUrl(result.ServerUrl)))
                    {
                        _logger.LogInformation($"Found printer at {result.IpAddress}:{result.Port} - {result.Name} ({result.Backend})", null, null);
                        // Exclude from future duplicates (within same run) in case multiple ports map
                        existingServerUrls.Add(NormalizeUrl(result.ServerUrl));
                    }
                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            DiscoveredPrinterDto?[] results = await Task.WhenAll(tasks);
            discovered.AddRange(results.Where(r => r != null)!);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Failed to scan network: {Network}", network);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Failed to scan network: {Network}", network);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to scan network: {Network}", network);
        }

        return discovered;
    }

    private async Task<List<DiscoveredPrinterDto>> ScanNetworkWithProgressAsync(string network, NetworkDiscoverySettingsDto settings, HashSet<string> existingServerUrls, string sessionId, int totalIps, int currentScannedStart, int currentFoundStart, bool autoDetectedNetworks, Action? onExcluded, CancellationToken cancellationToken)
    {
        List<DiscoveredPrinterDto> discovered = new();
        int scannedCount = 0;
        int foundCount = 0;

        try
        {
            (IPAddress? networkAddr, int cidr) = ParseCidr(network);
            List<string> hosts = GetHostsInRange(networkAddr, cidr);
            _logger.LogInformation($"Network {network} contains {hosts.Count} hosts to scan", null, null);

            using SemaphoreSlim semaphore = new(settings.MaxConcurrentScans, settings.MaxConcurrentScans);
            Task<DiscoveredPrinterDto?>[] tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogDebug($"Scanning host: {host}", null, null);

                    // Send progress update for current IP
                    int currentScanned = Interlocked.Increment(ref scannedCount);
                    DiscoveryProgressDto progressDto = new(
                        sessionId,
                        network,
                        host,
                        totalIps,
                        currentScannedStart + currentScanned,
                        currentFoundStart + foundCount,
                        0, // excluded count not tracked per-IP; real-time excluded aggregate not essential here
                        (double)(currentScannedStart + currentScanned) / totalIps * 100,
                        DiscoveryStatus.Scanning,
                        null,
                        settings.NetworkRanges,
                        autoDetectedNetworks
                    );
                    _progressCache.Set(sessionId, progressDto);
                    await _hubContext.Clients.Group($"discovery-{sessionId}").SendAsync("DiscoveryProgress", progressDto, cancellationToken);

                    DiscoveredPrinterDto? result = await ScanHostAsync(host, settings, existingServerUrls, cancellationToken);
                    if (result != null && !existingServerUrls.Contains(NormalizeUrl(result.ServerUrl)))
                    {
                        _logger.LogInformation($"Found printer at {result.IpAddress}:{result.Port} - {result.Name} ({result.Backend})", null, null);

                        // Increment found printers count
                        Interlocked.Increment(ref foundCount);

                        // Send printer found event
                        // Mark as seen to avoid duplicate notifications
                        existingServerUrls.Add(NormalizeUrl(result.ServerUrl));

                        await _hubContext.Clients.Group($"discovery-{sessionId}").SendAsync("DiscoveryPrinterFound", new DiscoveryPrinterFoundDto(sessionId, result), cancellationToken);
                    }
                    else if (result != null)
                    {
                        // It was discovered but excluded
                        onExcluded?.Invoke();
                    }
                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            DiscoveredPrinterDto?[] results = await Task.WhenAll(tasks);
            discovered.AddRange(results.Where(r => r != null)!);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Failed to scan network: {Network}", network);
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Failed to scan network: {Network}", network);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to scan network: {Network}", network);
        }

        return discovered;
    }

    private static (IPAddress network, int cidr) ParseCidr(string cidr)
    {
        string[] parts = cidr.Split('/');
        IPAddress network = IPAddress.Parse(parts[0]);
        int prefixLength = int.Parse(parts[1]);
        return (network, prefixLength);
    }

    private static List<string> GetHostsInRange(IPAddress network, int cidr)
    {
        List<string> hosts = new();

        try
        {
            byte[] networkBytes = network.GetAddressBytes();
            int hostBits = 32 - cidr;
            int hostCount = Math.Min((int)Math.Pow(2, hostBits) - 2, 254); // Limit to reasonable size

            for (int i = 1; i <= hostCount; i++)
            {
                byte[] hostBytes = (byte[])networkBytes.Clone();

                // For /24 networks, just increment the last octet
                // For /24 and other CIDR ranges currently supported, increment last octet
                hostBytes[3] = (byte)i;

                hosts.Add(new IPAddress(hostBytes).ToString());
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid CIDR range, return empty list
        }
        catch (OverflowException)
        {
            // Host count calculation overflow, return empty list
        }

        return hosts;
    }

    private async Task<DiscoveredPrinterDto?> ScanHostAsync(string ipAddress, NetworkDiscoverySettingsDto settings, HashSet<string> existingServerUrls, CancellationToken cancellationToken)
    {
        try
        {
            // Try configured ports in order
            foreach (int port in settings.Ports)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                DiscoveredPrinterDto? discovered = await TryDiscoverPrinterAsync(ipAddress, port, settings.TimeoutMs, settings.Backends, cancellationToken);
                if (discovered != null)
                {
                    // Filter out printers already in the system
                    if (existingServerUrls.Contains(NormalizeUrl(discovered.ServerUrl)))
                    {
                        // Already discovered, skip returning
                    }
                    else
                    {
                        return discovered;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to scan host {Host}", ipAddress);
        }

        return null;
    }

    private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(string ipAddress, int port, int timeoutMs, List<PrinterBackend>? backends, CancellationToken cancellationToken)
    {
        string baseUrl = $"http://{ipAddress}:{port}";

        // If no backends specified, scan all backends (default behavior)
        List<PrinterBackend> backendsToScan = backends ?? [PrinterBackend.Moonraker, PrinterBackend.PrusaLink];

        try
        {
            _logger.LogDebug("Attempting discovery at {BaseUrl}", baseUrl);
            
            // Try each backend in the list
            foreach (PrinterBackend backend in backendsToScan)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Port-specific backend scanning logic
                if (backend == PrinterBackend.Moonraker && port == 7125)
                {
                    _logger.LogInformation("Testing Moonraker at {BaseUrl}", baseUrl);
                    PrinterInfo? moonrakerInfo = await TryGetMoonrakerInfoAsync(baseUrl, timeoutMs, cancellationToken);
                    if (moonrakerInfo != null)
                    {
                        _logger.LogInformation("Successfully discovered Moonraker printer at {BaseUrl}", baseUrl);
                        return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.Moonraker, moonrakerInfo);
                    }
                    else
                    {
                        _logger.LogDebug("No Moonraker response from {BaseUrl}", baseUrl);
                    }
                }
                else if (backend == PrinterBackend.PrusaLink && port == 80)
                {
                    _logger.LogInformation("Testing PrusaLink at {BaseUrl}", baseUrl);
                    PrinterInfo? prusaInfo = await TryGetPrusaLinkInfoAsync(baseUrl, timeoutMs, cancellationToken);
                    if (prusaInfo != null)
                    {
                        _logger.LogInformation("Successfully discovered PrusaLink printer at {BaseUrl}", baseUrl);
                        return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.PrusaLink, prusaInfo);
                    }
                    else
                    {
                        _logger.LogDebug("No PrusaLink response from {BaseUrl}", baseUrl);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to discover printer at {BaseUrl}", baseUrl);
        }

        return null;
    }

    private async Task<PrinterInfo?> TryGetMoonrakerInfoAsync(string baseUrl, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            // Try to get printer info from Moonraker to get hostname and check if it's online
            MoonrakerPrinterInfo? printerInfo = await _moonrakerClient.GetPrinterInfoAsync(baseUrl, cts.Token);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Moonraker discovery failed for {BaseUrl}", baseUrl);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "Moonraker discovery failed for {BaseUrl}", baseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Moonraker discovery failed for {BaseUrl}", baseUrl);
        }

        return null;
    }

    private async Task<PrinterInfo?> TryGetPrusaLinkInfoAsync(string baseUrl, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            // Try to get info from PrusaLink API
            Services.PrinterInfo info = await _prusaLinkClient.ApiClient.GetInfoAsync(baseUrl, null, cts.Token);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "PrusaLink discovery failed for {BaseUrl}", baseUrl);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogDebug(ex, "PrusaLink discovery failed for {BaseUrl}", baseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "PrusaLink discovery failed for {BaseUrl}", baseUrl);
        }

        return null;
    }

    private static DiscoveredPrinterDto CreateDiscoveredPrinter(string ipAddress, int port, PrinterBackend backend, PrinterInfo info)
    {
        // For Moonraker printers on port 80, omit the port number from the URL for cleaner URLs
        string serverUrl = backend == PrinterBackend.Moonraker && port == 80
            ? $"http://{ipAddress}"
            : $"http://{ipAddress}:{port}";

        // Filter out "Unknown" values for manufacturer and model
        string? manufacturer = IsUnknownValue(info.Manufacturer) ? null : info.Manufacturer;
        string? model = IsUnknownValue(info.Model) ? null : info.Model;

        // If manufacturer is null, also set model to null
        if (manufacturer == null)
        {
            model = null;
        }

        return new DiscoveredPrinterDto
        {
            IpAddress = ipAddress,
            Port = port,
            BackendPort = backend == PrinterBackend.Moonraker ? 7125 : port,
            FrontendPort = backend == PrinterBackend.Moonraker ? 80 : port,
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
            Uri uri = new(url);
            return uri.Host;
        }
        catch (ArgumentException)
        {
            return "Unknown";
        }
        catch (UriFormatException)
        {
            return "Unknown";
        }
    }

    private sealed class PrinterInfo
    {
        public string? Name { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Firmware { get; set; }
        public string? Version { get; set; }
    }

    private static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }
        url = url.Trim();
        if (url.EndsWith('/'))
        {
            url = url.TrimEnd('/');
        }
        return url.ToLowerInvariant();
    }

    private HashSet<string> LoadExistingPrinterUrlsSafe()
    {
        HashSet<string> existingServerUrls = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Prefer a fresh scope so we don't depend on the lifetime of the injected scoped context (especially for background discovery)
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<string> urls = ctx.Printers.Select(p => p.ServerUrl).ToList();
            foreach (string? p in urls)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    existingServerUrls.Add(NormalizeUrl(p));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading existing printers for exclusion; proceeding without filter");
        }
        return existingServerUrls;
    }
}
