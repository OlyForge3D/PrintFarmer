
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Infrastructure.Settings;
using Farm.Web.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Services;

public interface INetworkDiscoveryService
{
    Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default);
    Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default);
}

public class NetworkDiscoveryService : INetworkDiscoveryService
{
    private readonly ISettingsService _settingsService;
    private readonly IHubContext<PrinterHub> _hubContext;
    private readonly IDiscoveryProgressCache _progressCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnifiedLoggingService _logger;
    private readonly IEnumerable<DiscoveryProbes.INetworkDiscoveryProbe> _probes;

    public NetworkDiscoveryService(
        ISettingsService settingsService,
        IHubContext<PrinterHub> hubContext,
        IDiscoveryProgressCache progressCache,
        IServiceScopeFactory scopeFactory,
        IUnifiedLoggingService logger,
        IEnumerable<DiscoveryProbes.INetworkDiscoveryProbe> probes)
    {
        _settingsService = settingsService;
        _hubContext = hubContext;
        _progressCache = progressCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _probes = probes;
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

    public async Task<List<DiscoveredPrinterDto>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting printer network discovery...");
        HashSet<string> existingServerUrls = LoadExistingPrinterUrlsSafe();
        var settings = _settingsService.Get<NetworkDiscoverySettings>() ?? new NetworkDiscoverySettings();
        if (!settings.EnableDiscovery || settings.DiscoverySubnets == null || settings.DiscoverySubnets.Count == 0)
        {
            _logger.LogWarning("Network discovery is disabled or no network ranges configured. Returning empty result.");
            return [];
        }
        _logger.LogInformation($"Discovery settings: Ranges={string.Join(",", settings.DiscoverySubnets)}, Enabled={settings.EnableDiscovery}");
        List<DiscoveredPrinterDto> discovered = new();
        foreach (var subnet in settings.DiscoverySubnets)
        {
            _logger.LogInformation($"Scanning network: {subnet}");
            var hosts = NetworkRangeHelper.ExpandNetworkRange(subnet, msg => _logger.LogWarning(msg)).ToList();
            List<DiscoveredPrinterDto> networkPrinters = await ScanNetworkAsync(hosts, existingServerUrls, settings.ClientTimeoutMs, cancellationToken);
            _logger.LogInformation($"Network {subnet} scan completed. Found {networkPrinters.Count} printers");
            discovered.AddRange(networkPrinters);
        }
        _logger.LogInformation($"Network discovery completed. Found {discovered.Count} printers");
        return discovered.OrderBy(p => p.IpAddress).ToList();
    }

    public async Task DiscoverPrintersWithProgressAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation($"[Discovery] Session {sessionId}: Starting printer network discovery (progress mode)...");
        HashSet<string> existingServerUrls = LoadExistingPrinterUrlsSafe();
        var settings = _settingsService.Get<NetworkDiscoverySettings>() ?? new NetworkDiscoverySettings();
        _logger.LogInformation($"[Discovery] Session {sessionId}: Using discovery settings: MaxConcurrentRequests={settings.MaxConcurrentRequests}, RequestDelayMs={settings.RequestDelayMs}, MaxRetries={settings.MaxRetries}");
        if (!settings.EnableDiscovery || settings.DiscoverySubnets == null || settings.DiscoverySubnets.Count == 0)
        {
            _logger.LogWarning($"[Discovery] Session {sessionId}: Network discovery is disabled or no network ranges configured. Streaming discovery will send immediate completion.");
            return;
        }
        _logger.LogInformation($"[Discovery] Session {sessionId}: Discovery settings: Ranges={string.Join(",", settings.DiscoverySubnets)}, Enabled={settings.EnableDiscovery}");

        int totalIps = 0;
        foreach (var subnet in settings.DiscoverySubnets)
        {
            try
            {
                var hosts = NetworkRangeHelper.ExpandNetworkRange(subnet, msg => _logger.LogWarning(msg)).ToList();
                totalIps += hosts.Count;
            }
            catch
            {
                // Invalid subnet, skip
            }
        }

        _logger.LogInformation($"[Discovery] Session {sessionId}: Total IPs to scan: {totalIps}");

        DiscoveryProgressDto initialProgress = new(
            sessionId,
            settings.DiscoverySubnets.FirstOrDefault() ?? string.Empty,
            string.Empty,
            totalIps,
            0,
            0,
            0,
            0d,
            DiscoveryStatus.Starting,
            null,
            settings.DiscoverySubnets.ToList(),
            false
        );
        _progressCache.Set(sessionId, initialProgress);
        await _hubContext.Clients
            .Group($"discovery-{sessionId}")
            .SendAsync("DiscoveryProgress", initialProgress, cancellationToken);

        // Scan all subnets and report progress incrementally
        List<DiscoveredPrinterDto> allDiscovered = new();
        int scannedIps = 0;
        int foundPrinters = 0;
        int excludedPrinters = 0;
        foreach (var subnet in settings.DiscoverySubnets)
        {
            var hosts = NetworkRangeHelper.ExpandNetworkRange(subnet, msg => _logger.LogWarning(msg)).ToList();
            _logger.LogInformation($"[Discovery] Session {sessionId}: Scanning subnet {subnet} with {hosts.Count} hosts");
            for (int i = 0; i < hosts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string host = hosts[i];
                // Retry loop for progress-based discovery
                DiscoveredPrinterDto? result = null;
                int attempt = 0;
                while (attempt <= settings.MaxRetries && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        result = await ScanHostAsync(host, existingServerUrls, settings.ClientTimeoutMs, cancellationToken);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, $"[Discovery] Session {sessionId}: Failed to scan host {host} on attempt {attempt + 1}");
                    }
                    attempt++;
                    if (attempt <= settings.MaxRetries)
                    {
                        await Task.Delay(settings.RequestDelayMs, cancellationToken);
                    }
                }
                scannedIps++;
                if (result != null && !existingServerUrls.Contains(NormalizeUrl(result.ServerUrl)))
                {
                    allDiscovered.Add(result);
                    foundPrinters++;
                    existingServerUrls.Add(NormalizeUrl(result.ServerUrl));
                    // Emit live printer event for frontend
                    await _hubContext.Clients
                        .Group($"discovery-{sessionId}")
                        .SendAsync("DiscoveryPrinterFound", new DiscoveryPrinterFoundDto(sessionId, result), cancellationToken);
                }
                // Send progress every 5 IPs or on last IP
                if (scannedIps == 1 || scannedIps % 5 == 0 || scannedIps == totalIps)
                {
                    var progress = new DiscoveryProgressDto(
                        sessionId,
                        subnet,
                        host,
                        totalIps,
                        scannedIps,
                        foundPrinters,
                        excludedPrinters,
                        totalIps > 0 ? (scannedIps * 100.0 / totalIps) : 0.0,
                        DiscoveryStatus.Scanning,
                        null,
                        settings.DiscoverySubnets.ToList(),
                        false
                    );
                    _progressCache.Set(sessionId, progress);
                    await _hubContext.Clients
                        .Group($"discovery-{sessionId}")
                        .SendAsync("DiscoveryProgress", progress, cancellationToken);
                }
            }
        }

        DiscoveryProgressDto finalProgress = new(
            sessionId,
            settings.DiscoverySubnets.FirstOrDefault() ?? string.Empty,
            string.Empty,
            totalIps,
            scannedIps,
            foundPrinters,
            excludedPrinters,
            100.0,
            DiscoveryStatus.Completed,
            null,
            settings.DiscoverySubnets.ToList(),
            false
        );
        _progressCache.Set(sessionId, finalProgress);
        await _hubContext.Clients
            .Group($"discovery-{sessionId}")
            .SendAsync("DiscoveryProgress", finalProgress, cancellationToken);
    }

    private async Task<List<DiscoveredPrinterDto>> ScanNetworkAsync(List<string> hosts, HashSet<string> existingServerUrls, int clientTimeoutMs, CancellationToken cancellationToken)
    {
        List<DiscoveredPrinterDto> discovered = new();

        try
        {
            _logger.LogInformation($"Network scan contains {hosts.Count} hosts to scan");
            var settings = _settingsService.Get<NetworkDiscoverySettings>() ?? new NetworkDiscoverySettings();
            _logger.LogInformation($"Using discovery settings: MaxConcurrentRequests={settings.MaxConcurrentRequests}, RequestDelayMs={settings.RequestDelayMs}, MaxRetries={settings.MaxRetries}");
            // Apply configured concurrency
            using SemaphoreSlim semaphore = new(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);
            Task<DiscoveredPrinterDto?>[] tasks = hosts.Select(async host =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    _logger.LogDebug($"Scanning host: {host}");
                    DiscoveredPrinterDto? result = null;
                    int attempt = 0;
                    // Retry loop
                    while (attempt <= settings.MaxRetries && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            result = await ScanHostAsync(host, existingServerUrls, clientTimeoutMs, cancellationToken);
                            if (result != null)
                            {
                                if (!existingServerUrls.Contains(NormalizeUrl(result.ServerUrl)))
                                {
                                    _logger.LogInformation($"Found printer at {result.IpAddress}:{result.Port} - {result.Name} ({result.Backend})");
                                    existingServerUrls.Add(NormalizeUrl(result.ServerUrl));
                                }
                                break;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, $"Failed to scan host {host} on attempt {attempt + 1}");
                        }
                        attempt++;
                        if (attempt <= settings.MaxRetries)
                        {
                            await Task.Delay(settings.RequestDelayMs, cancellationToken);
                        }
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to scan network");
        }

        return discovered;
    }

    private async Task<DiscoveredPrinterDto?> ScanHostAsync(string ipAddress, HashSet<string> existingServerUrls, int timeoutMs, CancellationToken cancellationToken)
    {
        // Use default ports and timeout
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            DiscoveredPrinterDto? discoveredPrinter = await TryDiscoverPrinterAsync(ipAddress, timeoutMs, cancellationToken);
            if (discoveredPrinter != null)
            {
                if (!existingServerUrls.Contains(NormalizeUrl(discoveredPrinter.ServerUrl)))
                {
                    return discoveredPrinter;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error scanning host {ipAddress}");
        }
        return null;
    }
    // Stub for TryDiscoverPrinterAsync to resolve build error
    private async Task<DiscoveredPrinterDto?> TryDiscoverPrinterAsync(string ipAddress, int timeoutMs, CancellationToken cancellationToken)
    {
        foreach (var probe in _probes)
        {
            try
            {
                var result = await probe.ProbeAsync(ipAddress, timeoutMs, cancellationToken);
                if (result != null)
                {
                    _logger.LogInformation($"{probe.DisplayName} backend discovered at {ipAddress}:{result.Port}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Probe {probe.DisplayName} failed for {ipAddress}");
            }
        }
        return null;
    }
}
