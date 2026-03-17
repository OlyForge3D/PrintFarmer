using System.Diagnostics;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Background service that monitors active print jobs for failures using AI-powered detection.
/// Periodically fetches camera snapshots from printers with active jobs and analyzes them
/// for print failures. Optionally pauses jobs when failures are detected.
/// </summary>
public sealed class PrintFailureMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPrinterStatusCacheReader _statusCache;
    private readonly IHubContext<PrinterHub> _hub;
    private readonly ILogger<PrintFailureMonitorService> _logger;
    private readonly ObicoSettings _settings;

    public PrintFailureMonitorService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IPrinterStatusCacheReader statusCache,
        IHubContext<PrinterHub> hub,
        IOptions<ObicoSettings> settings,
        ILogger<PrintFailureMonitorService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _statusCache = statusCache ?? throw new ArgumentNullException(nameof(statusCache));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[PrintFailureMonitor] Service starting");

        // Initial delay to allow database and printers to initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Only run monitoring if enabled in settings
                if (!_settings.Enabled)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                await RunMonitoringCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[PrintFailureMonitor] Service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PrintFailureMonitor] Unexpected error in monitoring loop");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("[PrintFailureMonitor] Service stopped");
    }

    private async Task RunMonitoringCycleAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        int checkedCount = 0;
        int failuresDetected = 0;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var failureDetectionService = scope.ServiceProvider.GetRequiredService<IObicoFailureDetectionService>();

            // Load ObicoServers once per cycle for efficient lookups
            Dictionary<Guid, ObicoServer> obicoServers = await dbContext.ObicoServers
                .Where(s => s.IsEnabled)
                .ToDictionaryAsync(s => s.Id, cancellationToken);

            // Find all printers with cameras configured
            List<Printer> printersWithCameras = await dbContext.Printers
                .Include(p => p.Cameras.Where(c => c.IsEnabled && !string.IsNullOrEmpty(c.SnapshotUrl)))
                .Where(p => p.Cameras.Any(c => c.IsEnabled && !string.IsNullOrEmpty(c.SnapshotUrl)))
                .ToListAsync(cancellationToken);

            if (printersWithCameras.Count == 0)
            {
                return;
            }

            // Filter to only printers that are actively printing
            List<Printer> activePrinters = printersWithCameras
                .Where(p => IsPrinterPrinting(p.Id))
                .ToList();

            if (activePrinters.Count == 0)
            {
                return;
            }

            _logger.LogDebug(
                "[PrintFailureMonitor] Checking {Count} printers with active jobs",
                activePrinters.Count);

            foreach (Printer printer in activePrinters)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    Camera? camera = printer.Cameras.FirstOrDefault();
                    if (camera?.SnapshotUrl == null)
                    {
                        continue;
                    }

                    // Determine which Obico server to use
                    string obicoServerUrl;
                    string? obicoApiKey = null;
                    if (printer.ObicoServerId.HasValue && obicoServers.TryGetValue(printer.ObicoServerId.Value, out ObicoServer? assignedServer))
                    {
                        obicoServerUrl = assignedServer.Url;
                        obicoApiKey = assignedServer.ApiKey;
                        _logger.LogDebug(
                            "[PrintFailureMonitor] Using assigned Obico server '{ServerName}' ({ServerUrl}) for printer {PrinterId} ({PrinterName})",
                            assignedServer.Name,
                            obicoServerUrl,
                            printer.Id,
                            printer.Name);
                    }
                    else
                    {
                        // Fallback to global settings URL for backward compatibility
                        obicoServerUrl = _settings.ObicoApiUrl;
                        _logger.LogDebug(
                            "[PrintFailureMonitor] Using default Obico server ({ServerUrl}) for printer {PrinterId} ({PrinterName})",
                            obicoServerUrl,
                            printer.Id,
                            printer.Name);
                    }

                    FailureDetectionResult result = await failureDetectionService.AnalyzeImageFromUrlAsync(
                        camera.SnapshotUrl,
                        obicoServerUrl,
                        obicoApiKey,
                        cancellationToken);

                    checkedCount++;

                    if (result.ErrorMessage != null)
                    {
                        _logger.LogWarning(
                            "[PrintFailureMonitor] Analysis failed for printer {PrinterId} ({PrinterName}): {Error}",
                            printer.Id,
                            printer.Name,
                            result.ErrorMessage);
                        continue;
                    }

                    if (result.IsFailureDetected)
                    {
                        failuresDetected++;
                        await HandleFailureDetectedAsync(printer, result, dbContext, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[PrintFailureMonitor] Error analyzing printer {PrinterId} ({PrinterName})",
                        printer.Id,
                        printer.Name);
                }
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "[PrintFailureMonitor] Cycle complete: {Checked} printers checked in {Elapsed}ms, {Failures} failures detected",
                checkedCount,
                stopwatch.ElapsedMilliseconds,
                failuresDetected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrintFailureMonitor] Failed to complete monitoring cycle");
        }
    }

    private bool IsPrinterPrinting(Guid printerId)
    {
        PrinterStatusDto? status = _statusCache.GetStatus(printerId);
        if (status == null)
        {
            return false;
        }

        return status.IsOnline &&
               !string.IsNullOrEmpty(status.State) &&
               string.Equals(status.State, "printing", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleFailureDetectedAsync(
        Printer printer,
        FailureDetectionResult result,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "[PrintFailureMonitor] Failure detected for printer {PrinterId} ({PrinterName}): confidence={Confidence:F3}",
            printer.Id,
            printer.Name,
            result.Confidence);

        // Find the current active print job
        PrintJob? currentJob = await dbContext.PrintJobs
            .Where(j =>
                j.AssignedPrinterId == printer.Id &&
                (j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting))
            .OrderByDescending(j => j.ActualStartTime ?? j.QueuedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Publish failure event to SignalR clients
        var failureEvent = new FailureDetectionDto
        {
            PrinterId = printer.Id,
            PrinterName = printer.Name,
            JobId = currentJob?.Id,
            Confidence = result.Confidence,
            DetectedAt = result.AnalyzedAt,
            AutoPaused = false
        };

        // Auto-pause the job if enabled and a job exists
        if (_settings.AutoPauseOnFailure && currentJob != null)
        {
            try
            {
                // Note: Actual pause logic would require calling the backend client
                // For now, we just log and broadcast. Full pause implementation
                // would need IBackendClientFactory to call the pause endpoint.
                _logger.LogWarning(
                    "[PrintFailureMonitor] Auto-pause is enabled but pause implementation requires backend client integration for job {JobId}",
                    currentJob.Id);

                failureEvent.AutoPaused = false; // Would be true after successful pause
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[PrintFailureMonitor] Failed to pause job {JobId} on printer {PrinterId}",
                    currentJob.Id,
                    printer.Id);
            }
        }

        // Broadcast failure event to all connected clients
        await _hub.Clients.All.SendAsync("FailureDetected", failureEvent, cancellationToken);

        _logger.LogInformation(
            "[PrintFailureMonitor] Failure event broadcast for printer {PrinterId}, job {JobId}",
            printer.Id,
            currentJob?.Id);
    }
}
