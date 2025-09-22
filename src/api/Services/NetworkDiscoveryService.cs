using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Farm.Web.Api.Data;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services;

public interface INetworkDiscoveryService
{
    Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default);
    Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default);
}

public partial class NetworkDiscoveryService(
    MoonrakerClient moonrakerClient,
    PrusaLinkClient prusaLinkClient,
    INetworkDiscoverySettingsService settingsService,
    IHubContext<PrinterHub> hubContext,
    IDiscoveryProgressCache progressCache,
    IServiceScopeFactory scopeFactory,
    ILogger<NetworkDiscoveryService> logger) : INetworkDiscoveryService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting printer network discovery...")]
    private static partial void LogDiscoveryStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discovery settings: Networks={Networks}, Timeout={TimeoutMs}ms, MaxScans={MaxScans}, Ports={Ports}")]
    private static partial void LogDiscoverySettings(ILogger logger, string networks, int timeoutMs, int maxScans, string ports);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanning network: {Network}")]
    private static partial void LogScanningNetwork(ILogger logger, string network);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network {Network} scan completed. Found {Count} printers")]
    private static partial void LogNetworkScanCompleted(ILogger logger, string network, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network discovery completed. Found {Count} printers")]
    private static partial void LogDiscoveryCompleted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Network {Network} contains {HostCount} hosts to scan")]
    private static partial void LogNetworkHostCount(ILogger logger, string network, int hostCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scanning host: {Host}")]
    private static partial void LogScanningHost(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "Found printer at {Host}:{Port} - {Name} ({Backend})")]
    private static partial void LogFoundPrinter(ILogger logger, string host, int port, string name, PrinterBackend backend);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to scan network: {Network}")]
    private static partial void LogNetworkScanError(ILogger logger, Exception exception, string network);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to scan host {Host}")]
    private static partial void LogHostScanError(ILogger logger, Exception exception, string host);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Attempting discovery at {BaseUrl}")]
    private static partial void LogAttemptingDiscovery(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Testing Moonraker at {BaseUrl}")]
    private static partial void LogTestingMoonraker(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully discovered Moonraker printer at {BaseUrl}")]
    private static partial void LogDiscoveredMoonraker(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No Moonraker response from {BaseUrl}")]
    private static partial void LogNoMoonrakerResponse(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Testing PrusaLink at {BaseUrl}")]
    private static partial void LogTestingPrusaLink(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully discovered PrusaLink printer at {BaseUrl}")]
    private static partial void LogDiscoveredPrusaLink(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No PrusaLink response from {BaseUrl}")]
    private static partial void LogNoPrusaLinkResponse(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Testing Moonraker on port 80 at {BaseUrl}")]
    private static partial void LogTestingMoonrakerPort80(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully discovered Moonraker printer on port 80 at {BaseUrl}")]
    private static partial void LogDiscoveredMoonrakerPort80(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No Moonraker response on port 80 from {BaseUrl}")]
    private static partial void LogNoMoonrakerResponsePort80(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to discover printer at {BaseUrl}")]
    private static partial void LogDiscoveryError(ILogger logger, Exception exception, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Moonraker discovery failed for {BaseUrl}")]
    private static partial void LogMoonrakerDiscoveryError(ILogger logger, Exception exception, string baseUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "PrusaLink discovery failed for {BaseUrl}")]
    private static partial void LogPrusaLinkDiscoveryError(ILogger logger, Exception exception, string baseUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting multi-network scan for {NetworkCount} networks: {Networks}")]
    private static partial void LogMultiNetworkScanStarting(ILogger logger, int networkCount, string networks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Progress after {Network}: {ScannedIps}/{TotalIps} IPs ({Progress:F1}%), {FoundPrinters} total")]
    private static partial void LogNetworkProgress(ILogger logger, string network, int scannedIps, int totalIps, double progress, int foundPrinters);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discovery cancelled during {Network} scan after {ScannedIps}/{TotalIps} IPs")]
    private static partial void LogDiscoveryCancelled(ILogger logger, string network, int scannedIps, int totalIps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to scan network {Network}, continuing with next network. Scanned {ScannedIps}/{TotalIps} IPs so far")]
    private static partial void LogNetworkScanContinue(ILogger logger, string network, int scannedIps, int totalIps);

    [LoggerMessage(Level = LogLevel.Information, Message = "Multi-network scan complete: {ScannedIps}/{TotalIps} IPs, {FoundPrinters} printers")]
    private static partial void LogMultiNetworkScanComplete(ILogger logger, int scannedIps, int totalIps, int foundPrinters);
    public async Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        LogDiscoveryStarting(logger);

        // Gather existing printers to exclude from results (fresh scope for safety)
        HashSet<string> existingServerUrls = LoadExistingPrinterUrlsSafe();

        NetworkDiscoverySettingsDto settings = settingsService.GetSettings();
        // Auto-detect network ranges if none configured
        if (settings.NetworkRanges.Count == 0)
        {
            List<string> autoRanges = DetectLocalNetworks().ToList();
            if (autoRanges.Count > 0)
            {
                settings.NetworkRanges.AddRange(autoRanges);
                logger.LogInformation("Auto-detected {Count} network range(s) for discovery: {Ranges}", autoRanges.Count, string.Join(",", autoRanges));
            }
            else
            {
                logger.LogWarning("No network ranges configured and none could be auto-detected. Discovery will return empty result.");
                return [];
            }
        }
        LogDiscoverySettings(logger, string.Join(",", settings.NetworkRanges), settings.TimeoutMs, settings.MaxConcurrentScans, string.Join(",", settings.Ports));

        List<DiscoveredPrinterDto> discovered = new();

        foreach (string network in settings.NetworkRanges)
        {
            LogScanningNetwork(logger, network);
            List<DiscoveredPrinterDto> networkPrinters = await ScanNetworkAsync(network, settings, existingServerUrls, cancellationToken);
            LogNetworkScanCompleted(logger, network, networkPrinters.Count);
            discovered.AddRange(networkPrinters);
        }

        LogDiscoveryCompleted(logger, discovered.Count);
        if (discovered.Count == 0)
        {
            return new List<DiscoveredPrinterDto>();
        }

        return discovered.OrderBy(p => p.IpAddress).ToList();
    }

    public async Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        LogDiscoveryStarting(logger);

        // Gather existing printers to exclude from streaming results (fresh scope - background task may outlive original request scope)
        HashSet<string> existingServerUrls = LoadExistingPrinterUrlsSafe();

        NetworkDiscoverySettingsDto settings = settingsService.GetSettings();
        bool autoDetectedNetworks = false;
        // Auto-detect network ranges if none configured
        if (settings.NetworkRanges.Count == 0)
        {
            List<string> autoRanges = DetectLocalNetworks().ToList();
            if (autoRanges.Count > 0)
            {
                settings.NetworkRanges.AddRange(autoRanges);
                autoDetectedNetworks = true;
                logger.LogInformation("Auto-detected {Count} network range(s) for streaming discovery: {Ranges}", autoRanges.Count, string.Join(",", autoRanges));
            }
            else
            {
                logger.LogWarning("No network ranges configured and none could be auto-detected. Streaming discovery will send immediate completion.");
            }
        }
        LogDiscoverySettings(logger, string.Join(",", settings.NetworkRanges), settings.TimeoutMs, settings.MaxConcurrentScans, string.Join(",", settings.Ports));

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
        progressCache.Set(sessionId, initialProgress);
        await hubContext.Clients
            .Group($"discovery-{sessionId}")
            .SendAsync(
                "DiscoveryProgress",
                initialProgress,
                cancellationToken);

        int excludedPrinters = 0; // count of printers skipped because already present

        LogMultiNetworkScanStarting(logger, settings.NetworkRanges.Count, string.Join(", ", settings.NetworkRanges));

        foreach (string network in settings.NetworkRanges)
        {
            LogScanningNetwork(logger, network);

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

                LogNetworkScanCompleted(logger, network, networkPrinters.Count);
                LogNetworkProgress(logger, network, scannedIps, totalIps, (double)scannedIps / totalIps * 100, foundPrinters);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Discovery cancelled during {Network} scan after {ScannedIps}/{TotalIps} IPs", network, scannedIps, totalIps);
                throw;
            }
            catch (Exception ex)
            {
                LogNetworkScanError(logger, ex, network);
                logger.LogWarning("Failed to scan network {Network}, continuing with next network. Scanned {ScannedIps}/{TotalIps} IPs so far", network, scannedIps, totalIps);
                // Continue with next network range instead of stopping entire discovery
                continue;
            }
        }

        logger.LogInformation("Multi-network scan complete: {ScannedIps}/{TotalIps} IPs, {FoundPrinters} printers", scannedIps, totalIps, foundPrinters);

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
            progressCache.Set(sessionId, finalProgress);
            await hubContext.Clients
                .Group($"discovery-{sessionId}")
                .SendAsync("DiscoveryProgress", finalProgress, cancellationToken);
        }

        // Send completion signal
        await hubContext.Clients
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

        LogDiscoveryCompleted(logger, foundPrinters);
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
            LogNetworkHostCount(logger, network, hosts.Count);

            using SemaphoreSlim semaphore = new(settings.MaxConcurrentScans, settings.MaxConcurrentScans);
            Task<DiscoveredPrinterDto?>[] tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    LogScanningHost(logger, host);
                    DiscoveredPrinterDto? result = await ScanHostAsync(host, settings, existingServerUrls, cancellationToken);
                    if (result != null && !existingServerUrls.Contains(NormalizeUrl(result.ServerUrl)))
                    {
                        LogFoundPrinter(logger, result.IpAddress, result.Port, result.Name, result.Backend);
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
            LogNetworkScanError(logger, ex, network);
        }
        catch (ArgumentException ex)
        {
            LogNetworkScanError(logger, ex, network);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNetworkScanError(logger, ex, network);
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
            LogNetworkHostCount(logger, network, hosts.Count);

            using SemaphoreSlim semaphore = new(settings.MaxConcurrentScans, settings.MaxConcurrentScans);
            Task<DiscoveredPrinterDto?>[] tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    LogScanningHost(logger, host);

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
                    progressCache.Set(sessionId, progressDto);
                    await hubContext.Clients.Group($"discovery-{sessionId}").SendAsync("DiscoveryProgress", progressDto, cancellationToken);

                    DiscoveredPrinterDto? result = await ScanHostAsync(host, settings, existingServerUrls, cancellationToken);
                    if (result != null && !existingServerUrls.Contains(NormalizeUrl(result.ServerUrl)))
                    {
                        LogFoundPrinter(logger, result.IpAddress, result.Port, result.Name, result.Backend);

                        // Increment found printers count
                        Interlocked.Increment(ref foundCount);

                        // Send printer found event
                        // Mark as seen to avoid duplicate notifications
                        existingServerUrls.Add(NormalizeUrl(result.ServerUrl));

                        await hubContext.Clients.Group($"discovery-{sessionId}").SendAsync("DiscoveryPrinterFound", new DiscoveryPrinterFoundDto(sessionId, result), cancellationToken);
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
            LogNetworkScanError(logger, ex, network);
        }
        catch (ArgumentException ex)
        {
            LogNetworkScanError(logger, ex, network);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNetworkScanError(logger, ex, network);
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

                DiscoveredPrinterDto? discovered = await TryDiscoverPrinterAsync(ipAddress, port, settings.TimeoutMs, cancellationToken);
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
            LogHostScanError(logger, ex, ipAddress);
        }

        return null;
    }

    private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(string ipAddress, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        string baseUrl = $"http://{ipAddress}:{port}";

        try
        {
            LogAttemptingDiscovery(logger, baseUrl);
            // Only allow Moonraker discovery on port 7125
            if (port == 7125)
            {
                LogTestingMoonraker(logger, baseUrl);
                PrinterInfo? moonrakerInfo = await TryGetMoonrakerInfoAsync(baseUrl, timeoutMs, cancellationToken);
                if (moonrakerInfo != null)
                {
                    LogDiscoveredMoonraker(logger, baseUrl);
                    return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.Moonraker, moonrakerInfo);
                }
                else
                {
                    LogNoMoonrakerResponse(logger, baseUrl);
                }
            }
            else if (port == 80)
            {
                // Only allow PrusaLink on port 80. Ignore Moonraker on port 80.
                LogTestingPrusaLink(logger, baseUrl);
                PrinterInfo? prusaInfo = await TryGetPrusaLinkInfoAsync(baseUrl, timeoutMs, cancellationToken);
                if (prusaInfo != null)
                {
                    LogDiscoveredPrusaLink(logger, baseUrl);
                    return CreateDiscoveredPrinter(ipAddress, port, PrinterBackend.PrusaLink, prusaInfo);
                }
                else
                {
                    LogNoPrusaLinkResponse(logger, baseUrl);
                    // Do NOT test for Moonraker on port 80 anymore. Intentionally skip.
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
            LogDiscoveryError(logger, ex, baseUrl);
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
            MoonrakerPrinterInfo? printerInfo = await moonrakerClient.GetPrinterInfoAsync(baseUrl, cts.Token);
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
            LogMoonrakerDiscoveryError(logger, ex, baseUrl);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogMoonrakerDiscoveryError(logger, ex, baseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMoonrakerDiscoveryError(logger, ex, baseUrl);
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
            Services.PrinterInfo info = await prusaLinkClient.ApiClient.GetInfoAsync(baseUrl, null, cts.Token);
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
            LogPrusaLinkDiscoveryError(logger, ex, baseUrl);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            LogPrusaLinkDiscoveryError(logger, ex, baseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPrusaLinkDiscoveryError(logger, ex, baseUrl);
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
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext ctx = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
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
            logger.LogWarning(ex, "Failed loading existing printers for exclusion; proceeding without filter");
        }
        return existingServerUrls;
    }
}
