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

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Background service that monitors active print jobs for failures using AI-powered detection.
/// Periodically fetches camera snapshots from printers with active jobs and analyzes them
/// for print failures. Optionally pauses jobs when failures are detected.
/// </summary>
public sealed class PrintFailureMonitorService : BackgroundService
{
    private const string AnalysisOutcomeError = "error";
    private const string AnalysisOutcomeFailure = "failure";
    private const string AnalysisOutcomeHealthy = "healthy";
    private const string AnalysisOutcomeNone = "none";
    private const string DetectionSourceGlobal = "global";
    private const string DetectionSourceNone = "none";
    private const string DetectionSourcePooled = "pooled";
    private const string PrinterStateDisabled = "disabled";
    private const string PrinterStateError = "error";
    private const string PrinterStateIdle = "idle";
    private const string PrinterStateMisconfigured = "misconfigured";
    private const string PrinterStateMonitoring = "monitoring";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFailureDetectionMonitorStatus _monitorStatus;
    private readonly IPrinterStatusCacheReader _statusCache;
    private readonly IHubContext<PrinterHub> _hub;
    private readonly ILogger<PrintFailureMonitorService> _logger;

    public PrintFailureMonitorService(
        IServiceScopeFactory scopeFactory,
        IFailureDetectionMonitorStatus monitorStatus,
        IPrinterStatusCacheReader statusCache,
        IHubContext<PrinterHub> hub,
        ILogger<PrintFailureMonitorService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _monitorStatus = monitorStatus ?? throw new ArgumentNullException(nameof(monitorStatus));
        _statusCache = statusCache ?? throw new ArgumentNullException(nameof(statusCache));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ObicoSettings currentSettings = GetCurrentSettings();
        _monitorStatus.UpdateSnapshot(new FailureDetectionMonitorStatusDto
        {
            MonitoringEnabled = currentSettings.Enabled,
            ConfidenceThreshold = currentSettings.ConfidenceThreshold,
            ScanIntervalSeconds = currentSettings.ScanIntervalSeconds,
            AutoPauseOnFailure = currentSettings.AutoPauseOnFailure,
        });
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
                await Task.Delay(TimeSpan.FromSeconds(GetCurrentSettings().ScanIntervalSeconds), stoppingToken);
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
        DateTime cycleStartedAt = DateTime.UtcNow;
        FailureDetectionMonitorStatusDto previousSnapshot = _monitorStatus.GetSnapshot();
        Dictionary<Guid, FailureDetectionPrinterStatusDto> previousPrinterStatuses = previousSnapshot.Printers
            .ToDictionary(status => status.PrinterId);
        int activeCount = 0;
        int checkedCount = 0;
        int failuresDetected = 0;

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var failureDetectionService = scope.ServiceProvider.GetRequiredService<IObicoFailureDetectionService>();
            var printersService = scope.ServiceProvider.GetRequiredService<IPrintersService>();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

            ObicoSettings currentSettings = settingsService.Get<ObicoSettings>();
            Dictionary<Guid, ObicoServer> obicoServers = currentSettings.Enabled
                ? await dbContext.ObicoServers
                    .Where(s => s.IsEnabled)
                    .ToDictionaryAsync(s => s.Id, cancellationToken)
                : [];

            List<Printer> configuredPrinters = await dbContext.Printers
                .Include(p => p.Cameras.Where(c => c.IsEnabled))
                .Where(p => p.ObicoEnabled)
                .OrderBy(p => p.Name)
                .ToListAsync(cancellationToken);

            var printerStatuses = new List<FailureDetectionPrinterStatusDto>(configuredPrinters.Count);
            foreach (Printer printer in configuredPrinters)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                FailureDetectionPrinterStatusDto? fallbackStatus = null;
                try
                {
                    previousPrinterStatuses.TryGetValue(printer.Id, out FailureDetectionPrinterStatusDto? previousStatus);

                    // Prefer Camera entities, but fall back to legacy printer fields if no cameras exist
                    Camera? camera = printer.Cameras.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.SnapshotUrl));
                    string? snapshotUrl = camera?.SnapshotUrl;

                    // Fallback to legacy printer field if no Camera entities have snapshot URLs
                    if (string.IsNullOrWhiteSpace(snapshotUrl))
                    {
                        snapshotUrl = printer.CameraSnapshotUrl;
                    }

                    bool isPrinting = IsPrinterPrinting(printer.Id);
                    var (detectionSource, detectionTarget, obicoServerUrl, obicoApiKey) = ResolveDetectionTarget(printer, obicoServers, currentSettings);

                    var baseStatus = new FailureDetectionPrinterStatusDto
                    {
                        PrinterId = printer.Id,
                        PrinterName = printer.Name,
                        DetectionSource = detectionSource,
                        DetectionTarget = detectionTarget,
                        IsPrinting = isPrinting,
                        SnapshotUrl = snapshotUrl,
                        LastAnalyzedAt = previousStatus?.LastAnalyzedAt,
                        LastOutcome = previousStatus?.LastOutcome ?? AnalysisOutcomeNone,
                        LastConfidence = previousStatus?.LastConfidence,
                        LastAutoPaused = previousStatus?.LastAutoPaused,
                        LastFailureDetectedAt = previousStatus?.LastFailureDetectedAt,
                    };
                    fallbackStatus = baseStatus with
                    {
                        State = PrinterStateError,
                        Reason = "Monitoring request failed.",
                        LastOutcome = AnalysisOutcomeError,
                    };

                    if (!currentSettings.Enabled)
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = PrinterStateDisabled,
                            Reason = "Failure detection is disabled in Settings.",
                        });
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(snapshotUrl))
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = PrinterStateMisconfigured,
                            Reason = "Camera snapshot URL required. Add or enable a camera in printer settings.",
                        });
                        continue;
                    }

                    if (!isPrinting)
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = PrinterStateIdle,
                            Reason = "Printer is not actively printing.",
                        });
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(obicoServerUrl))
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = PrinterStateError,
                            Reason = "No Obico server configured. Contact your administrator to configure Obico integration.",
                        });
                        continue;
                    }

                    activeCount++;
                    var monitoringStatus = baseStatus with
                    {
                        State = PrinterStateMonitoring,
                        Reason = detectionSource == DetectionSourcePooled
                            ? $"Monitoring via pooled server '{detectionTarget}'."
                            : "Monitoring via global Obico ML settings.",
                    };

                    FailureDetectionResult result = await failureDetectionService.AnalyzeImageFromUrlAsync(
                        snapshotUrl,
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
                        printerStatuses.Add(monitoringStatus with
                        {
                            State = PrinterStateError,
                            Reason = result.ErrorMessage,
                            LastAnalyzedAt = result.AnalyzedAt,
                            LastOutcome = AnalysisOutcomeError,
                        });
                        continue;
                    }

                    if (result.IsFailureDetected)
                    {
                        failuresDetected++;
                        bool autoPaused = await HandleFailureDetectedAsync(
                            printer,
                            snapshotUrl,
                            result,
                            currentSettings,
                            dbContext,
                            printersService,
                            cancellationToken);
                        printerStatuses.Add(monitoringStatus with
                        {
                            LastAnalyzedAt = result.AnalyzedAt,
                            LastOutcome = AnalysisOutcomeFailure,
                            LastConfidence = result.Confidence,
                            LastAutoPaused = autoPaused,
                            LastFailureDetectedAt = result.AnalyzedAt,
                            Reason = autoPaused
                                ? "Failure detected and print auto-paused."
                                : "Failure detected.",
                        });
                        continue;
                    }

                    printerStatuses.Add(monitoringStatus with
                    {
                        LastAnalyzedAt = result.AnalyzedAt,
                        LastOutcome = AnalysisOutcomeHealthy,
                        LastConfidence = result.Confidence,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[PrintFailureMonitor] Error analyzing printer {PrinterId} ({PrinterName})",
                        printer.Id,
                        printer.Name);
                    printerStatuses.Add((fallbackStatus ?? new FailureDetectionPrinterStatusDto
                    {
                        PrinterId = printer.Id,
                        PrinterName = printer.Name,
                    }) with
                    {
                        State = PrinterStateError,
                        Reason = "Monitoring request failed.",
                        LastOutcome = AnalysisOutcomeError,
                    });
                }
            }

            stopwatch.Stop();
            _monitorStatus.UpdateSnapshot(CreateSnapshot(
                currentSettings,
                configuredPrinters.Count,
                activeCount,
                checkedCount,
                failuresDetected,
                cycleStartedAt,
                DateTime.UtcNow,
                null,
                printerStatuses.ToArray()));
            _logger.LogInformation(
                "[PrintFailureMonitor] Cycle complete: {Configured} configured, {Active} active, {Checked} printers checked in {Elapsed}ms, {Failures} failures detected",
                configuredPrinters.Count,
                activeCount,
                checkedCount,
                stopwatch.ElapsedMilliseconds,
                failuresDetected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PrintFailureMonitor] Failed to complete monitoring cycle");
            ObicoSettings currentSettings = GetCurrentSettings();
            _monitorStatus.UpdateSnapshot(new FailureDetectionMonitorStatusDto
            {
                MonitoringEnabled = currentSettings.Enabled,
                ConfidenceThreshold = currentSettings.ConfidenceThreshold,
                ScanIntervalSeconds = currentSettings.ScanIntervalSeconds,
                AutoPauseOnFailure = currentSettings.AutoPauseOnFailure,
                ConfiguredPrinterCount = previousSnapshot.ConfiguredPrinterCount,
                ActivelyMonitoredPrinterCount = previousSnapshot.ActivelyMonitoredPrinterCount,
                LastAnalyzedPrinterCount = previousSnapshot.LastAnalyzedPrinterCount,
                LastFailureCount = previousSnapshot.LastFailureCount,
                LastScanStartedAt = cycleStartedAt,
                LastScanCompletedAt = DateTime.UtcNow,
                LastError = ex.Message,
                Printers = previousSnapshot.Printers.Select(status => status with { }).ToArray(),
            });
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

    private FailureDetectionMonitorStatusDto CreateSnapshot(
        ObicoSettings currentSettings,
        int configuredPrinterCount,
        int activelyMonitoredPrinterCount,
        int lastAnalyzedPrinterCount,
        int lastFailureCount,
        DateTime lastScanStartedAt,
        DateTime lastScanCompletedAt,
        string? lastError,
        FailureDetectionPrinterStatusDto[] printers) =>
        new()
        {
            MonitoringEnabled = currentSettings.Enabled,
            ConfidenceThreshold = currentSettings.ConfidenceThreshold,
            ScanIntervalSeconds = currentSettings.ScanIntervalSeconds,
            AutoPauseOnFailure = currentSettings.AutoPauseOnFailure,
            ConfiguredPrinterCount = configuredPrinterCount,
            ActivelyMonitoredPrinterCount = activelyMonitoredPrinterCount,
            LastAnalyzedPrinterCount = lastAnalyzedPrinterCount,
            LastFailureCount = lastFailureCount,
            LastScanStartedAt = lastScanStartedAt,
            LastScanCompletedAt = lastScanCompletedAt,
            LastError = lastError,
            Printers = printers,
        };

    private (string DetectionSource, string? DetectionTarget, string? ObicoServerUrl, string? ObicoApiKey) ResolveDetectionTarget(
        Printer printer,
        Dictionary<Guid, ObicoServer> obicoServers,
        ObicoSettings currentSettings)
    {
        if (printer.ObicoServerId.HasValue &&
            obicoServers.TryGetValue(printer.ObicoServerId.Value, out ObicoServer? assignedServer))
        {
            return (DetectionSourcePooled, assignedServer.Name, assignedServer.Url, assignedServer.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(currentSettings.ObicoApiUrl))
        {
            return (DetectionSourceGlobal, currentSettings.ObicoApiUrl, currentSettings.ObicoApiUrl, null);
        }

        return (DetectionSourceNone, null, null, null);
    }

    private async Task<bool> HandleFailureDetectedAsync(
        Printer printer,
        string? snapshotUrl,
        FailureDetectionResult result,
        ObicoSettings currentSettings,
        AppDbContext dbContext,
        IPrintersService printersService,
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
            SnapshotUrl = snapshotUrl,
            AutoPaused = false
        };

        // Auto-pause the job if enabled and a job exists
        if (currentSettings.AutoPauseOnFailure && currentJob != null)
        {
            try
            {
                failureEvent.AutoPaused = await printersService.PauseAsync(printer.Id, cancellationToken);
                if (failureEvent.AutoPaused)
                {
                    _logger.LogWarning(
                        "[PrintFailureMonitor] Auto-paused printer {PrinterId} after failure detection for job {JobId}",
                        printer.Id,
                        currentJob.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "[PrintFailureMonitor] Auto-pause failed for printer {PrinterId} after failure detection for job {JobId}",
                        printer.Id,
                        currentJob.Id);
                }
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

        return failureEvent.AutoPaused;
    }

    private ObicoSettings GetCurrentSettings()
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        return settingsService.Get<ObicoSettings>();
    }
}
