using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;

namespace Farm.Shared.Discovery;

/// <summary>
/// Core printer discovery service using probe-based detection.
/// Runs all available discovery probes and selects the result with highest confidence.
/// This service is infrastructure-agnostic and can be used by any consumer (API, CLI, microservice).
/// </summary>
public interface ICoreNetworkDiscoveryService
{
    /// <summary>
    /// Attempt to discover a printer at the specified IP address using all available probes.
    /// Returns the result with the highest confidence score, or null if no probe matches.
    /// </summary>
    Task<DiscoveredPrinterDto?> DiscoverPrinterAsync(
        string ipAddress,
        int timeoutMs,
        IEnumerable<PrinterBackend>? backendFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discover multiple printers concurrently across a list of IP addresses.
    /// </summary>
    Task<List<DiscoveredPrinterDto>> DiscoverMultipleAsync(
        IEnumerable<string> ipAddresses,
        int timeoutMs,
        int maxConcurrent = 50,
        IEnumerable<PrinterBackend>? backendFilter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Core implementation of network discovery using probes.
/// </summary>
public class CoreNetworkDiscoveryService : ICoreNetworkDiscoveryService
{
    private readonly IEnumerable<INetworkDiscoveryProbe> _discoveryProbes;
    private readonly ILogger<CoreNetworkDiscoveryService>? _logger;

    public CoreNetworkDiscoveryService(
        IEnumerable<INetworkDiscoveryProbe> discoveryProbes,
        ILogger<CoreNetworkDiscoveryService>? logger = null)
    {
        _discoveryProbes = discoveryProbes ?? throw new ArgumentNullException(nameof(discoveryProbes));
        _logger = logger;
    }

    /// <summary>
    /// Discover a single printer at the given IP address.
    /// </summary>
    public async Task<DiscoveredPrinterDto?> DiscoverPrinterAsync(
        string ipAddress,
        int timeoutMs,
        IEnumerable<PrinterBackend>? backendFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        // Determine which probes to run
        IEnumerable<INetworkDiscoveryProbe> probesToRun = _discoveryProbes;
        if (backendFilter != null)
        {
            var backends = backendFilter.ToList();
            if (backends.Count > 0)
            {
                var backendSet = new HashSet<PrinterBackend>(backends);
                probesToRun = probesToRun.Where(p => backendSet.Contains(p.Backend));
            }
        }

        // Run all probes and collect results with scores
        List<ProbeResult> results = new();
        foreach (INetworkDiscoveryProbe probe in probesToRun)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                _logger?.LogDebug($"Trying probe {probe.DisplayName} for {ipAddress}");
                var result = await probe.ProbeAsync(ipAddress, timeoutMs, cancellationToken);
                if (result != null)
                {
                    _logger?.LogDebug($"Probe {probe.DisplayName} matched for {ipAddress} with confidence {result.ConfidenceScore} ({result.Reason})");
                    results.Add(result);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, $"Probe {probe.DisplayName} failed for {ipAddress}");
            }
        }

        // If no probes matched, return null
        if (results.Count == 0)
        {
            return null;
        }

        // Select the result with the highest confidence score
        ProbeResult bestResult = results.MaxBy(r => r.ConfidenceScore)!;
        _logger?.LogInformation($"Successfully discovered {bestResult.Printer.Backend} printer at {ipAddress} ({bestResult.Reason})");
        return bestResult.Printer;
    }

    /// <summary>
    /// Discover printers at multiple IP addresses concurrently.
    /// </summary>
    public async Task<List<DiscoveredPrinterDto>> DiscoverMultipleAsync(
        IEnumerable<string> ipAddresses,
        int timeoutMs,
        int maxConcurrent = 50,
        IEnumerable<PrinterBackend>? backendFilter = null,
        CancellationToken cancellationToken = default)
    {
        var discovered = new List<DiscoveredPrinterDto>();
        var ips = ipAddresses?.ToList() ?? new List<string>();

        if (ips.Count == 0)
        {
            return discovered;
        }

        using var semaphore = new SemaphoreSlim(maxConcurrent);
        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await DiscoverPrinterAsync(ip, timeoutMs, backendFilter, cancellationToken);
                return result;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        discovered.AddRange(results.Where(r => r != null)!);
        return discovered;
    }
}
