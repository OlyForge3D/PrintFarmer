using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services;

public interface INetworkDiscoveryService
{
    Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default);
    Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default);
    Task DiscoverPrintersWithProgressAsync(string sessionId, IEnumerable<PrinterBackend>? backends, CancellationToken cancellationToken = default);
}

public partial class NetworkDiscoveryService(
    ISettingsService settingsService,
    IHubContext<PrinterHub> hubContext,
    IDiscoveryProgressCache progressCache,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IEnumerable<DiscoveryProbes.INetworkDiscoveryProbe> discoveryProbes) : INetworkDiscoveryService
{
    private readonly ISettingsService _settingsService = settingsService;
    private readonly IHubContext<PrinterHub> _hubContext = hubContext;
    private readonly IDiscoveryProgressCache _progressCache = progressCache;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IEnumerable<DiscoveryProbes.INetworkDiscoveryProbe> _discoveryProbes = discoveryProbes;

    public async Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting printer network discovery...", null, null);

        // Gather existing printers to exclude from results (fresh scope for safety)
        HashSet<string> existingServerUrls = await LoadExistingPrinterUrlsSafeAsync();

        // Load app settings (NetworkDiscoverySettings is an AppSetting) and map to the DTO used by discovery
        NetworkDiscoverySettingsDto settings = GetDiscoverySettings();
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
                return new List<DiscoveredPrinterDto>();
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

    public async Task DiscoverPrintersWithProgressAsync(string sessionId, IEnumerable<PrinterBackend>? backends, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"Starting printer network discovery...", null, null);

        // Gather existing printers to exclude from streaming results (fresh scope - background task may outlive original request scope)
        HashSet<string> existingServerUrls = await LoadExistingPrinterUrlsSafeAsync();

        // Load and map discovery settings from AppSetting class
        NetworkDiscoverySettingsDto settings = GetDiscoverySettings();

        // Override backends if provided in the request
        if (backends != null)
        {
            List<PrinterBackend> backendList = backends.ToList();
            if (backendList.Count > 0)
            {
                settings = settings with { Backends = backendList };
            }
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
                _logger.LogInformation("Auto-detected network ranges for streaming discovery", null, new { autoRanges.Count, Ranges = autoRanges });
            }
            else
            {
                _logger.LogWarning("No network ranges configured and none could be auto-detected. Streaming discovery will send immediate completion.", null, null);
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
                _logger.LogError(ex, "Failed to scan network: {Network}", null, new { Network = network });
                _logger.LogWarning("Failed to scan network, continuing with next network.", null, new { Network = network, ScannedIps = scannedIps, TotalIps = totalIps });
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
                    _ = results.Add(cidrString);
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
                        _ = existingServerUrls.Add(NormalizeUrl(result.ServerUrl));
                    }
                    return result;
                }
                finally
                {
                    _ = semaphore.Release();
                }
            }).ToArray();

            DiscoveredPrinterDto?[] results = await Task.WhenAll(tasks);
            discovered.AddRange(results.Where(r => r != null)!);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Failed to scan network", null, new { Network = network });
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Failed to scan network", null, new { Network = network });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to scan network", null, new { Network = network });
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
                        _ = Interlocked.Increment(ref foundCount);

                        // Send printer found event
                        // Mark as seen to avoid duplicate notifications
                        _ = existingServerUrls.Add(NormalizeUrl(result.ServerUrl));

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
                    _ = semaphore.Release();
                }
            }).ToArray();

            DiscoveredPrinterDto?[] results = await Task.WhenAll(tasks);
            discovered.AddRange(results.Where(r => r != null)!);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Failed to scan network", null, new { Network = network });
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Failed to scan network", null, new { Network = network });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to scan network", null, new { Network = network });
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
            // Use discovery probes to attempt printer detection (each probe knows its own ports)
            DiscoveredPrinterDto? discovered = await TryDiscoverPrinterAsync(ipAddress, settings.TimeoutMs, cancellationToken);
            if (discovered != null)
            {
                // Filter out printers already in the system
                if (existingServerUrls.Contains(NormalizeUrl(discovered.ServerUrl)))
                {
                    // Already discovered, skip returning
                    return null;
                }
                else
                {
                    return discovered;
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

    private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        // Use discovery probes to attempt printer detection
        foreach (DiscoveryProbes.INetworkDiscoveryProbe probe in _discoveryProbes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                _logger.LogDebug($"Trying probe {probe.DisplayName} for {ipAddress}", null, null);
                DiscoveredPrinterDto? result = await probe.ProbeAsync(ipAddress, timeoutMs, cancellationToken);
                if (result != null)
                {
                    _logger.LogInformation($"Successfully discovered {result.Backend} printer at {ipAddress} using {probe.DisplayName}", null, null);
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected when cancellation is requested
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, $"Probe {probe.DisplayName} failed for {ipAddress}");
            }
        }

        return null;
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
        // Keep original casing but trimmed/normalized for comparison. Callers use
        // case-insensitive collections (StringComparer.OrdinalIgnoreCase) when
        // comparing normalized URLs, so lower-casing here is not required.
        return url;
    }

    private async Task<HashSet<string>> LoadExistingPrinterUrlsSafeAsync()
    {
        HashSet<string> existingServerUrls = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Prefer a fresh async scope so we don't depend on the lifetime of the injected scoped context (especially for background discovery)
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            AppDbContext ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<string> urls = await ctx.Printers.Select(p => p.ServerUrl).ToListAsync();
            foreach (string? p in urls)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    _ = existingServerUrls.Add(NormalizeUrl(p));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading existing printers for exclusion; proceeding without filter");
        }
        return existingServerUrls;
    }

    // Map the application AppSetting class to the DTO used by the discovery service.
    // This isolates the discovery code from the internal AppSetting type and prevents
    // callers from attempting to request the DTO type directly from ISettingsService.
    private NetworkDiscoverySettingsDto GetDiscoverySettings()
    {
        try
        {
            // NetworkDiscoverySettings is registered as an AppSetting and should be available
            NetworkDiscoverySettings app = _settingsService.Get<NetworkDiscoverySettings>();
            IList<string> ranges = app.DiscoverySubnets ?? new List<string>();
            IList<int> ports = app.Ports ?? new List<int> { 80 };
            // NetworkDiscoverySettingsDto expects concrete List<T> types; convert if necessary.
            List<string> rangesList = ranges is List<string> lr ? lr : ranges.ToList();
            List<int> portsList = ports is List<int> lp ? lp : ports.ToList();
            return new NetworkDiscoverySettingsDto(rangesList, app.ClientTimeoutMs, app.MaxConcurrentRequests, portsList, null);
        }
        catch (Exception ex)
        {
            // If mapping fails for any reason, log and fall back to the DTO defaults
            _logger.LogWarning(ex, "Failed to load NetworkDiscoverySettings from settings service; falling back to defaults");
            return new NetworkDiscoverySettingsDto();
        }
    }
}
