using System.Net;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;

namespace PrinterDiscovery.Services;

/// <summary>
/// Service for streaming discovery with real-time progress updates via SignalR.
/// Extends the base discovery functionality with progress broadcasting.
/// </summary>
public interface IStreamingDiscoveryService
{
    /// <summary>
    /// Start a streaming discovery scan with progress updates.
    /// </summary>
    /// <param name="sessionId">The session ID for tracking progress</param>
    /// <param name="backends">Optional filter for specific printer backends</param>
    /// <param name="autoRegister">Whether to automatically register discovered printers</param>
    /// <param name="subnets">List of subnets to scan (CIDR notation)</param>
    /// <param name="probeTimeoutMs">Timeout for each probe in milliseconds</param>
    /// <param name="maxConcurrentProbes">Maximum concurrent probes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered printers</returns>
    Task<IReadOnlyList<DiscoveredPrinterDto>> ScanWithProgressAsync(
        string sessionId,
        IEnumerable<PrinterBackend>? backends = null,
        bool autoRegister = true,
        IEnumerable<string>? subnets = null,
        int? probeTimeoutMs = null,
        int? maxConcurrentProbes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel an active discovery session.
    /// </summary>
    void CancelSession(string sessionId);
}

public class StreamingDiscoveryService : IStreamingDiscoveryService
{
    private readonly ICoreNetworkDiscoveryService _coreDiscovery;
    private readonly IApiClient _apiClient;
    private readonly IDiscoveryProgressBroadcaster _broadcaster;
    private readonly IDiscoverySessionManager _sessionManager;
    private readonly ILogger<StreamingDiscoveryService> _logger;
    private readonly IConfiguration _config;
    private readonly int _probeTimeoutMs;

    public StreamingDiscoveryService(
        ICoreNetworkDiscoveryService coreDiscovery,
        IApiClient apiClient,
        IDiscoveryProgressBroadcaster broadcaster,
        IDiscoverySessionManager sessionManager,
        ILogger<StreamingDiscoveryService> logger,
        IConfiguration config)
    {
        _coreDiscovery = coreDiscovery ?? throw new ArgumentNullException(nameof(coreDiscovery));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _probeTimeoutMs = _config.GetValue("Discovery:ProbeTimeoutMs", 200);
    }

    public async Task<IReadOnlyList<DiscoveredPrinterDto>> ScanWithProgressAsync(
        string sessionId,
        IEnumerable<PrinterBackend>? backends = null,
        bool autoRegister = true,
        IEnumerable<string>? subnets = null,
        int? probeTimeoutMs = null,
        int? maxConcurrentProbes = null,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionManager.RegisterSession(sessionId, cts);

        try
        {
            _logger.LogInformation("[STREAMING-DISCOVERY] Starting scan for session {SessionId}", sessionId);

            // Use provided subnets or fall back to config
            string[] subnetArray;
            if (subnets != null)
            {
                subnetArray = subnets.ToArray();
                if (subnetArray.Length > 0)
                {
                    _logger.LogInformation("[STREAMING-DISCOVERY] Using provided subnets: {Subnets}", string.Join(", ", subnetArray));
                }
                else
                {
                    string subnetsConfig = _config["Discovery:Subnets"] ?? "10.0.0.0/24";
                    subnetArray = subnetsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    _logger.LogInformation("[STREAMING-DISCOVERY] Using config subnets (empty provided): {Subnets}", string.Join(", ", subnetArray));
                }
            }
            else
            {
                string subnetsConfig = _config["Discovery:Subnets"] ?? "10.0.0.0/24";
                subnetArray = subnetsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries);
                _logger.LogInformation("[STREAMING-DISCOVERY] Using config subnets: {Subnets}", string.Join(", ", subnetArray));
            }

            // Use provided timeout or fall back to config
            int timeout = probeTimeoutMs ?? _probeTimeoutMs;
            _logger.LogInformation("[STREAMING-DISCOVERY] Probe timeout: {Timeout}ms", timeout);

            // Use provided max concurrent or fall back to config
            int maxConcurrent = maxConcurrentProbes ?? _config.GetValue("Discovery:MaxConcurrentProbes", 50);
            _logger.LogInformation("[STREAMING-DISCOVERY] Max concurrent probes: {MaxConcurrent}", maxConcurrent);

            // Log backends filter
            if (backends != null && backends.Any())
            {
                _logger.LogInformation("[STREAMING-DISCOVERY] Backend filter: {Backends}", string.Join(", ", backends));
            }
            else
            {
                _logger.LogInformation("[STREAMING-DISCOVERY] No backend filter - scanning all backends");
            }

            List<string> ipAddresses = GenerateIpAddresses(subnetArray.ToList());

            int totalIps = ipAddresses.Count;
            _logger.LogInformation("[STREAMING-DISCOVERY] Scanning {Count} IPs for session {SessionId}", totalIps, sessionId);

            // Fetch already registered printers to exclude from discovery results
            HashSet<string> registeredUrls = await _apiClient.GetRegisteredPrinterUrlsAsync(cts.Token);
            _logger.LogInformation("[STREAMING-DISCOVERY] Excluding {Count} already registered printers", registeredUrls.Count);

            // Broadcast initial progress
            await _broadcaster.BroadcastProgressAsync(new DiscoveryProgressDto(
                SessionId: sessionId,
                CurrentNetwork: subnetArray.FirstOrDefault() ?? "Unknown",
                CurrentIp: "",
                TotalIps: totalIps,
                ScannedIps: 0,
                PrintersFound: 0,
                PrintersExcluded: 0,
                ProgressPercentage: 0,
                Status: DiscoveryStatus.Scanning,
                Message: $"Scanning {totalIps} IP addresses..."
            ), cts.Token);

            List<DiscoveredPrinterDto> discovered = new();
            int excluded = 0;
            int scanned = 0;
            DateTime startTime = DateTime.UtcNow;
            DateTime lastProgressUpdate = DateTime.UtcNow;

            // Use batched parallel scanning with progress updates
            SemaphoreSlim semaphore = new(maxConcurrent);
            List<Task> tasks = new();

            foreach (string ip in ipAddresses)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    break;
                }

                await semaphore.WaitAsync(cts.Token);

                Task task = Task.Run(async () =>
                {
                    try
                    {
                        DiscoveredPrinterDto? result = await _coreDiscovery.DiscoverPrinterAsync(
                            ip,
                            timeout,
                            backends,
                            cts.Token);

                        if (result != null)
                        {
                            // Check if this printer is already registered
                            if (registeredUrls.Contains(result.ServerUrl))
                            {
                                _logger.LogDebug("[STREAMING-DISCOVERY] Skipping already registered printer at {Ip}: {Name}", ip, result.Name);
                                Interlocked.Increment(ref excluded);
                                return;
                            }

                            lock (discovered)
                            {
                                discovered.Add(result);
                            }

                            _logger.LogInformation("[STREAMING-DISCOVERY] Found NEW printer at {Ip}: {Name}", ip, result.Name);

                            // Broadcast printer found event immediately
                            await _broadcaster.BroadcastPrinterFoundAsync(
                                new DiscoveryPrinterFoundDto(sessionId, result),
                                cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancelled
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[STREAMING-DISCOVERY] Error probing {Ip}", ip);
                    }
                    finally
                    {
                        int currentScanned = Interlocked.Increment(ref scanned);
                        semaphore.Release();

                        // Broadcast progress every 100ms or every 10 IPs
                        if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(100) || currentScanned % 10 == 0)
                        {
                            lastProgressUpdate = DateTime.UtcNow;
                            int percentage = (int)((double)currentScanned / totalIps * 100);

                            try
                            {
                                await _broadcaster.BroadcastProgressAsync(new DiscoveryProgressDto(
                                    SessionId: sessionId,
                                    CurrentNetwork: subnetArray.FirstOrDefault() ?? "Unknown",
                                    CurrentIp: ip,
                                    TotalIps: totalIps,
                                    ScannedIps: currentScanned,
                                    PrintersFound: discovered.Count,
                                    PrintersExcluded: excluded,
                                    ProgressPercentage: percentage,
                                    Status: DiscoveryStatus.Scanning,
                                    Message: $"Scanned {currentScanned}/{totalIps} - Found {discovered.Count} printers"
                                ), CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "[STREAMING-DISCOVERY] Failed to broadcast progress");
                            }
                        }
                    }
                }, cts.Token);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            TimeSpan duration = DateTime.UtcNow - startTime;

            // Auto-register if requested
            if (autoRegister && discovered.Count > 0)
            {
                _logger.LogInformation("[STREAMING-DISCOVERY] Registering {Count} discovered printers", discovered.Count);
                foreach (DiscoveredPrinterDto printer in discovered)
                {
                    try
                    {
                        await _apiClient.RegisterDiscoveredPrinterAsync(printer, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[STREAMING-DISCOVERY] Failed to register printer {Name}", printer.Name);
                    }
                }
            }

            // Broadcast completion
            DiscoveryStatus finalStatus = cts.Token.IsCancellationRequested
                ? DiscoveryStatus.Cancelled
                : DiscoveryStatus.Completed;

            string completionMessage = excluded > 0
                ? $"Discovery complete - Found {discovered.Count} new printers ({excluded} already registered) in {duration.TotalSeconds:F1}s"
                : $"Discovery complete - Found {discovered.Count} printers in {duration.TotalSeconds:F1}s";

            await _broadcaster.BroadcastProgressAsync(new DiscoveryProgressDto(
                SessionId: sessionId,
                CurrentNetwork: "Complete",
                CurrentIp: "",
                TotalIps: totalIps,
                ScannedIps: scanned,
                PrintersFound: discovered.Count,
                PrintersExcluded: excluded,
                ProgressPercentage: 100,
                Status: finalStatus,
                Message: completionMessage
            ), CancellationToken.None);

            await _broadcaster.BroadcastCompletedAsync(new DiscoveryCompletedDto(
                SessionId: sessionId,
                TotalPrintersFound: discovered.Count,
                TotalPrintersExcluded: excluded,
                Duration: duration,
                WasCancelled: cts.Token.IsCancellationRequested
            ), CancellationToken.None);

            _logger.LogInformation("[STREAMING-DISCOVERY] Session {SessionId} completed: {Count} new printers, {Excluded} excluded in {Duration:F1}s",
                sessionId, discovered.Count, excluded, duration.TotalSeconds);

            return discovered;
        }
        finally
        {
            _sessionManager.RemoveSession(sessionId);
        }
    }

    public void CancelSession(string sessionId)
    {
        _sessionManager.CancelSession(sessionId);
    }

    private static List<string> GenerateIpAddresses(List<string> subnets)
    {
        List<string> ips = new();

        foreach (string subnet in subnets)
        {
            try
            {
                string trimmed = subnet.Trim();
                if (!trimmed.Contains('/'))
                {
                    continue;
                }

                SubnetParser network = SubnetParser.Parse(trimmed);
                // Limit to reasonable number of addresses
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
