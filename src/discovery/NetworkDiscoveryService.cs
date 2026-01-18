using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Farm.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Discovery;

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
public class CoreNetworkDiscoveryService(
    IEnumerable<INetworkDiscoveryProbe> discoveryProbes,
    ILogger<CoreNetworkDiscoveryService>? logger = null) : ICoreNetworkDiscoveryService
{
    private readonly IEnumerable<INetworkDiscoveryProbe> _discoveryProbes = discoveryProbes ?? throw new ArgumentNullException(nameof(discoveryProbes));
    private readonly ILogger<CoreNetworkDiscoveryService>? _logger = logger;

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
            List<PrinterBackend> backends = backendFilter.ToList();
            if (backends.Count > 0)
            {
                HashSet<PrinterBackend> backendSet = new HashSet<PrinterBackend>(backends);
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
                _logger?.LogDebug("Trying probe {ProbeName} for {IpAddress}", probe.DisplayName, ipAddress);
                ProbeResult? result = await probe.ProbeAsync(ipAddress, timeoutMs, cancellationToken);
                if (result != null)
                {
                    _logger?.LogDebug("Probe {ProbeName} matched for {IpAddress} with confidence {ConfidenceScore} ({Reason})", probe.DisplayName, ipAddress, result.ConfidenceScore, result.Reason);
                    results.Add(result);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Probe {ProbeName} failed for {IpAddress}", probe.DisplayName, ipAddress);
            }
        }

        // If no probes matched, return null
        if (results.Count == 0)
        {
            return null;
        }

        // Select the result with the highest confidence score
        ProbeResult bestResult = results.MaxBy(r => r.ConfidenceScore)!;
        _logger?.LogInformation("Successfully discovered {Backend} printer at {IpAddress} ({Reason})", bestResult.Printer.Backend, ipAddress, bestResult.Reason);
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
        List<DiscoveredPrinterDto> discovered = new List<DiscoveredPrinterDto>();
        List<string> ips = ipAddresses?.ToList() ?? new List<string>();

        if (ips.Count == 0)
        {
            return discovered;
        }

        using SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrent);
        IEnumerable<Task<DiscoveredPrinterDto?>> tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                DiscoveredPrinterDto? result = await DiscoverPrinterAsync(ip, timeoutMs, backendFilter, cancellationToken);
                return result;
            }
            finally
            {
                _ = semaphore.Release();
            }
        });

        DiscoveredPrinterDto?[] results = await Task.WhenAll(tasks);
        discovered.AddRange(results.Where(r => r != null)!);
        return discovered;
    }
}
