using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cameras;
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
/// for print failures and reports detections for operator review.
/// </summary>
public sealed class PrintFailureMonitorService : BackgroundService
{
    private const string AnalysisOutcomeError = "error";
    private const string AnalysisOutcomeFailure = "failure";
    private const string AnalysisOutcomeHealthy = "healthy";
    private const string AnalysisOutcomeNone = "none";
    private const string DetectionSourceNone = "none";
    private const string PrinterStateDisabled = "disabled";
    private const string PrinterStateError = "error";
    private const string PrinterStateIdle = "idle";
    private const string PrinterStateMisconfigured = "misconfigured";
    private const string PrinterStateMonitoring = "monitoring";
    private const string PrinterStateReservationHeld = "reservation-held";
    private const string PrinterStateUnknown = "unknown";
    private const string StartingIdleReason = "Print is starting — monitoring will begin after warmup.";
    private const string WarmupIdleReason = "Print just started — monitoring will begin shortly.";
    private const string NotPrintingReason = "Printer is not actively printing.";
    private const string UnknownActivityReason = "Printer activity is unknown or offline. Monitoring is not active; the print may still be running.";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFailureDetectionMonitorStatus _monitorStatus;
    private readonly IPrinterStatusCacheReader _statusCache;
    private readonly IHubContext<PrinterHub> _hub;
    private readonly FailureDetectionMetrics _metrics;
    private readonly ILogger<PrintFailureMonitorService> _logger;

    public PrintFailureMonitorService(
        IServiceScopeFactory scopeFactory,
        IFailureDetectionMonitorStatus monitorStatus,
        IPrinterStatusCacheReader statusCache,
        IHubContext<PrinterHub> hub,
        FailureDetectionMetrics metrics,
        ILogger<PrintFailureMonitorService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _monitorStatus = monitorStatus ?? throw new ArgumentNullException(nameof(monitorStatus));
        _statusCache = statusCache ?? throw new ArgumentNullException(nameof(statusCache));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ObicoSettings currentSettings = GetCurrentSettings();
        PrintGuardShadowSettings printGuardSettings = GetCurrentPrintGuardSettings();
        _monitorStatus.UpdateSnapshot(new FailureDetectionMonitorStatusDto
        {
            MonitoringEnabled = currentSettings.Enabled || printGuardSettings.Enabled,
            ConfidenceThreshold = currentSettings.ConfidenceThreshold,
            ScanIntervalSeconds = currentSettings.ScanIntervalSeconds,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[PrintFailureMonitor] Service starting");

        // Initial delay to allow database and printers to initialize
        await Task.Delay(TimeSpan.FromSeconds(GetCurrentSettings().StartupDelaySeconds), stoppingToken);

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

    private sealed record ActivePrintWindow(Guid Id, PrintJobStatus Status, DateTime StartedAtUtc, string? JobName, bool IsExternal);

    internal static ObicoPrintActivity ResolvePrintActivity(
        Guid printerId,
        PrinterStatusDto? status,
        Guid? jobId,
        PrintJobStatus? jobStatus,
        bool isExternalJob = false)
    {
        string externalKey = "external:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(status?.JobName ?? status?.FileName ?? string.Empty)));
        bool livePrint = status?.IsOnline == true && PrinterStateClassifier.IsActivePrintingJob(status.State);
        bool starting = jobStatus == PrintJobStatus.Starting;
        if (starting || livePrint)
        {
            return new(printerId, jobId.HasValue ? $"job:{jobId:N}" : externalKey,
                AdoptExternalKey: isExternalJob ? externalKey : null);
        }

        return new(printerId, null, RetainExisting: IsActivityUnknown(status));
    }

    private static bool IsActivityUnknown([NotNullWhen(false)] PrinterStatusDto? status)
    {
        if (status is null || !status.IsOnline || string.IsNullOrWhiteSpace(status.State))
        {
            return true;
        }

        // A non-printing label is not necessarily confirmed inactivity (e.g. SDCP "unknown").
        return !PrinterStateClassifier.IsActivePrintingJob(status.State)
            && status.State.Trim().ToUpperInvariant() is not
                ("IDLE" or "READY" or "OPERATIONAL" or "STANDBY" or "COMPLETE" or "COMPLETED"
                or "FINISHED" or "STOPPED" or "CANCELLED" or "CANCELED" or "ERROR" or "FAILED");
    }

    internal static (bool ShouldMonitor, string IdleReason) EvaluateMonitoringWindow(
        PrinterStatusDto? status,
        PrintJobStatus? activeJobStatus,
        DateTime? activeJobStartedAtUtc,
        DateTime utcNow,
        int warmupGracePeriodSeconds = 120)
    {
        if (IsActivityUnknown(status))
        {
            return (false, UnknownActivityReason);
        }

        if (!PrinterStateClassifier.IsActivePrintingJob(status.State))
        {
            return (false, NotPrintingReason);
        }

        if (activeJobStatus == PrintJobStatus.Starting)
        {
            return (false, StartingIdleReason);
        }

        if (activeJobStatus == PrintJobStatus.Printing
            && activeJobStartedAtUtc.HasValue
            && activeJobStartedAtUtc.Value >= utcNow - TimeSpan.FromSeconds(warmupGracePeriodSeconds))
        {
            return (false, WarmupIdleReason);
        }

        return (true, string.Empty);
    }

    /// <summary>
    /// Resolves the operator-facing job/file context for failure-detection status and alerts.
    /// </summary>
    internal static (string? JobName, string? FileName) ResolveJobContext(
        PrinterStatusDto? printerStatus,
        string? activeJobName)
    {
        string? jobName = string.IsNullOrWhiteSpace(printerStatus?.JobName)
            ? activeJobName
            : printerStatus.JobName;

        string? fileName = string.IsNullOrWhiteSpace(printerStatus?.FileName)
            ? PrinterStatusDto.ExtractFileName(jobName)
            : printerStatus.FileName;

        return (jobName, fileName);
    }

    /// <summary>
    /// Loads provider-enabled printers with their camera links (and linked cameras) so camera
    /// selection reads the <see cref="CameraPrinter"/> join rather than a possibly unloaded collection.
    /// </summary>
    internal static Task<List<Printer>> LoadMonitoredPrintersAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        return dbContext.Printers
            .Include(p => p.CameraLinks)
                .ThenInclude(l => l.Camera)
            .Where(p => p.ObicoEnabled || p.PrintGuardEnabled)
            .OrderBy(p => p.Name)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Selects the camera used for failure detection: the printer's primary linked camera first, then link/camera
    /// sort order, preferring an enabled, non-offline camera with a snapshot URL and falling back to the next one.
    /// </summary>
    internal static PrinterCameraSelection SelectDetectionCamera(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        return PrinterCameraSelector.SelectSnapshotCamera(PrinterCameraSelector.OrderCameras(printer.CameraLinks));
    }

    /// <summary>
    /// Builds the user-facing reason shown when no usable detection camera is available.
    /// </summary>
    internal static string DescribeCameraUnavailable(PrinterCameraSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Availability switch
        {
            PrinterCameraAvailability.NoCameraLinked =>
                "No camera linked to this printer. Link a camera in printer settings.",
            PrinterCameraAvailability.AllCamerasDisabled =>
                "All cameras linked to this printer are disabled. Enable a camera in printer settings.",
            PrinterCameraAvailability.NoSnapshotUrl =>
                "Camera snapshot URL required. None of this printer's enabled cameras has a snapshot URL.",
            PrinterCameraAvailability.CamerasOffline => selection.Camera is { HealthMessage: { } health } camera
                && !string.IsNullOrWhiteSpace(health)
                    ? $"Camera '{camera.Name}' is offline: {health}"
                    : $"Camera '{selection.Camera?.Name}' is offline.",
            _ => string.Empty,
        };
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
            var incidentHistoryService = scope.ServiceProvider.GetRequiredService<IFailureDetectionIncidentHistoryService>();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            PrintGuardShadowSettings printGuardSettings = scope.ServiceProvider
                .GetRequiredService<IOptionsMonitor<PrintGuardShadowSettings>>()
                .CurrentValue;
            Uri? printGuardEndpoint = null;
            bool printGuardEnabled = printGuardSettings.Enabled
                && printGuardSettings.TryGetClassificationEndpoint(out printGuardEndpoint);

            if (printGuardSettings.Enabled && !printGuardEnabled)
            {
                _logger.LogWarning(
                    "[PrintFailureMonitor] PrintGuard is enabled but its endpoint or timeout configuration is invalid");
            }

            ObicoSettings currentSettings = settingsService.Get<ObicoSettings>();
            List<Printer> configuredPrinters = await LoadMonitoredPrintersAsync(dbContext, cancellationToken);
            List<Guid> configuredPrinterIds = configuredPrinters.Select(p => p.Id).ToList();
            List<PrintJob> activeJobs = configuredPrinterIds.Count == 0
                ? []
                : await dbContext.PrintJobs
                    .WhereOccupiesPrinter()
                    .Where(j =>
                        j.AssignedPrinterId.HasValue
                        && configuredPrinterIds.Contains(j.AssignedPrinterId.Value))
                    .ToListAsync(cancellationToken);
            Dictionary<Guid, ActivePrintWindow> activeJobsByPrinter = activeJobs
                .GroupBy(j => j.AssignedPrinterId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        PrintJob currentJob = g
                            .OrderByDescending(j => j.ActualStartTime ?? j.UpdatedAt)
                            .First();
                        DateTime startedAtUtc = currentJob.ActualStartTime ?? currentJob.UpdatedAt;
                        return new ActivePrintWindow(currentJob.Id, currentJob.Status, startedAtUtc, currentJob.Name, currentJob.IsExternalPrint);
                    });
            Dictionary<Guid, PrinterStatusDto?> cachedStatuses = configuredPrinters
                .ToDictionary(printer => printer.Id, printer => _statusCache.GetStatus(printer.Id));

            IReadOnlyDictionary<Guid, ObicoPrintRoute> obicoRoutes;
            bool obicoRoutingFailed = false;
            try
            {
                var routing = scope.ServiceProvider.GetRequiredService<IObicoServerAssignmentService>();
                ObicoPrintActivity[] activities = configuredPrinters.Where(printer => printer.ObicoEnabled)
                    .Select(printer =>
                    {
                        activeJobsByPrinter.TryGetValue(printer.Id, out ActivePrintWindow? job);
                        return ResolvePrintActivity(printer.Id, cachedStatuses[printer.Id], job?.Id, job?.Status, job?.IsExternal == true);
                    }).ToArray();
                obicoRoutes = await routing.ReconcileAsync(activities, currentSettings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PrintFailureMonitor] Obico route reconciliation failed; printing and other providers continue");
                obicoRoutingFailed = true;
                obicoRoutes = configuredPrinters.ToDictionary(
                    printer => printer.Id,
                    _ => new ObicoPrintRoute("none", null, null, null,
                        "Obico routing failed. Printing continues without Obico monitoring; check server logs."));
            }

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

                    PrinterCameraSelection cameraSelection = SelectDetectionCamera(printer);
                    string? snapshotUrl = cameraSelection.Camera?.SnapshotUrl;

                    activeJobsByPrinter.TryGetValue(printer.Id, out ActivePrintWindow? activeJob);
                    PrinterStatusDto? cachedPrinterStatus = cachedStatuses[printer.Id];
                    var monitoringWindow = EvaluateMonitoringWindow(
                        cachedPrinterStatus,
                        activeJob?.Status,
                        activeJob?.StartedAtUtc,
                        DateTime.UtcNow,
                        currentSettings.WarmupGracePeriodSeconds);
                    bool isPrinting = monitoringWindow.ShouldMonitor;
                    bool activityUnknown = monitoringWindow.IdleReason == UnknownActivityReason;
                    var jobContext = ResolveJobContext(cachedPrinterStatus, activeJob?.JobName);
                    bool obicoSelected = printer.ObicoEnabled;
                    bool printGuardSelected = printer.PrintGuardEnabled;
                    obicoRoutes.TryGetValue(printer.Id, out ObicoPrintRoute? obicoRoute);
                    string obicoDetectionSource = obicoRoute?.Source ?? DetectionSourceNone;
                    string? obicoDetectionTarget = activityUnknown && obicoRoute?.Source == "global"
                        ? "Global Obico server" : obicoRoute?.Target;
                    string? obicoServerUrl = obicoRoute?.Url;
                    string? obicoApiKey = obicoRoute?.ApiKey;
                    (string detectionSource, string? detectionTarget) = ResolveProviderTargets(
                        obicoSelected,
                        printGuardSelected,
                        obicoDetectionSource,
                        obicoDetectionTarget);
                    var providerStatuses = new List<FailureDetectionProviderStatusDto>(2);

                    if (obicoSelected)
                    {
                        bool obicoConfigured = !string.IsNullOrWhiteSpace(obicoServerUrl);
                        string obicoReason = !currentSettings.Enabled
                            ? "Obico is disabled in Settings."
                            : obicoConfigured ? string.Empty
                                : obicoRoute?.UnavailableReason ?? "Obico monitoring unavailable. Printing continues.";
                        FailureDetectionProviderStatusDto obicoStatus = CreateInitialProviderStatus(
                            "Obico",
                            currentSettings.Enabled && obicoConfigured,
                            obicoReason,
                            previousStatus,
                            AnalysisOutcomeNone);
                        providerStatuses.Add(currentSettings.Enabled && !obicoConfigured && isPrinting
                            ? obicoStatus with { State = PrinterStateError, LastOutcome = AnalysisOutcomeError }
                            : obicoStatus);
                    }

                    if (printGuardSelected)
                    {
                        string printGuardReason = !printGuardSettings.Enabled
                            ? "PrintGuard is disabled in deployment configuration. Set PFARM__PrintGuardShadow__Enabled=true and configure PFARM__PrintGuardShadow__ApiUrl."
                            : printGuardEnabled
                                ? string.Empty
                                : "PrintGuard endpoint or timeout configuration is invalid.";
                        providerStatuses.Add(CreateInitialProviderStatus(
                            "PrintGuard",
                            printGuardEnabled,
                            printGuardReason,
                            previousStatus,
                            AnalysisOutcomeNone));
                    }

                    bool obicoReady = obicoSelected
                        && currentSettings.Enabled
                        && !string.IsNullOrWhiteSpace(obicoServerUrl);
                    bool printGuardReady = printGuardSelected && printGuardEnabled;
                    int readyProviderCount = (obicoReady ? 1 : 0) + (printGuardReady ? 1 : 0);

                    var baseStatus = new FailureDetectionPrinterStatusDto
                    {
                        PrinterId = printer.Id,
                        PrinterName = printer.Name,
                        DetectionSource = detectionSource,
                        DetectionTarget = detectionTarget,
                        IsPrinting = isPrinting,
                        JobName = jobContext.JobName,
                        FileName = jobContext.FileName,
                        SnapshotUrl = snapshotUrl,
                        LastAnalyzedAt = previousStatus?.LastAnalyzedAt,
                        LastOutcome = previousStatus?.LastOutcome ?? AnalysisOutcomeNone,
                        LastConfidence = previousStatus?.LastConfidence,
                        LastFailureDetectedAt = previousStatus?.LastFailureDetectedAt,
                        Providers = providerStatuses.ToArray(),
                    };
                    fallbackStatus = baseStatus with
                    {
                        State = PrinterStateError,
                        Reason = "Monitoring request failed.",
                        LastOutcome = AnalysisOutcomeError,
                    };

                    if (activityUnknown)
                    {
                        bool reservationHeld = obicoRoute?.Source is "pooled" or "global";
                        string unknownReason = reservationHeld && obicoRoute?.UnavailableReason is null
                            ? "Printer activity is unknown or offline. Obico reservation retained; monitoring is not active. The print may still be running."
                                + (string.IsNullOrWhiteSpace(obicoDetectionTarget) ? string.Empty : $" Reserved server: {obicoDetectionTarget}.")
                            : obicoRoute?.UnavailableReason ?? UnknownActivityReason;
                        printerStatuses.Add(baseStatus with
                        {
                            State = reservationHeld ? PrinterStateReservationHeld : PrinterStateUnknown,
                            DetectionSource = reservationHeld ? obicoDetectionSource : detectionSource,
                            DetectionTarget = reservationHeld ? obicoDetectionTarget : detectionTarget,
                            Reason = obicoSelected && currentSettings.Enabled
                                ? unknownReason
                                : UnknownActivityReason,
                            Providers = providerStatuses.Select(provider => provider with
                            {
                                State = provider.Provider == "Obico" && currentSettings.Enabled
                                    ? obicoRoutingFailed || obicoRoute?.SelectedServerUnavailable == true
                                        ? PrinterStateError
                                        : reservationHeld ? PrinterStateReservationHeld : PrinterStateUnknown
                                    : provider.State == "ready" ? PrinterStateUnknown : provider.State,
                                Reason = provider.Provider == "Obico" && currentSettings.Enabled
                                    ? unknownReason
                                    : provider.State == "ready" ? UnknownActivityReason : provider.Reason,
                            }).ToArray(),
                        });
                        continue;
                    }

                    if (readyProviderCount == 0)
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = isPrinting && currentSettings.Enabled && obicoSelected ? PrinterStateError
                                : isPrinting ? PrinterStateDisabled : PrinterStateIdle,
                            Reason = !isPrinting ? monitoringWindow.IdleReason
                                : obicoRoute?.UnavailableReason ?? "No selected failure-detection provider is enabled and configured.",
                        });
                        continue;
                    }

                    if (cameraSelection.Availability != PrinterCameraAvailability.Available || string.IsNullOrWhiteSpace(snapshotUrl))
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = cameraSelection.Availability == PrinterCameraAvailability.CamerasOffline
                                ? PrinterStateError
                                : PrinterStateMisconfigured,
                            Reason = DescribeCameraUnavailable(cameraSelection),
                        });
                        continue;
                    }

                    if (!isPrinting)
                    {
                        printerStatuses.Add(baseStatus with
                        {
                            State = PrinterStateIdle,
                            Reason = monitoringWindow.IdleReason,
                        });
                        continue;
                    }

                    activeCount++;
                    checkedCount++;
                    byte[]? imageData = null;
                    string? captureError = null;
                    if (printGuardReady)
                    {
                        int captureTimeoutSeconds = readyProviderCount == 2
                            ? Math.Min(currentSettings.AnalysisTimeoutSeconds, printGuardSettings.CaptureTimeoutSeconds)
                            : printGuardSettings.CaptureTimeoutSeconds;
                        ICameraSnapshotCaptureService snapshotCapture = scope.ServiceProvider
                            .GetRequiredService<ICameraSnapshotCaptureService>();
                        CameraSnapshotCaptureResult capture = await snapshotCapture.CaptureAsync(
                            snapshotUrl,
                            captureTimeoutSeconds,
                            cancellationToken);

                        if (capture.ErrorMessage is not null || capture.ImageData is null)
                        {
                            _metrics.RecordError();
                            captureError = capture.ErrorMessage ?? "Camera snapshot capture failed.";
                            _logger.LogWarning("[PrintFailureMonitor] Snapshot capture failed for printer {PrinterId} ({PrinterName}): {Error}", printer.Id, printer.Name, captureError);
                            imageData = null;
                        }
                        else if (!FailureDetectionImageValidator.IsJpeg(capture.ImageData))
                        {
                            captureError = "Captured camera image is not a JPEG.";
                            _metrics.RecordError();
                            imageData = null;
                        }
                        else
                        {
                            imageData = capture.ImageData;
                        }

                        if (captureError is not null)
                        {
                            providerStatuses = providerStatuses
                                .Select(status => status.Provider == "PrintGuard"
                                    ? status with
                                    {
                                        State = PrinterStateError,
                                        Reason = captureError,
                                        LastOutcome = AnalysisOutcomeError,
                                    }
                                    : status)
                                .ToList();
                        }
                    }

                    var analyzedProviders = new List<FailureDetectionProviderStatusDto>(readyProviderCount);
                    string pairedAnalysisMode = readyProviderCount == 2 && imageData is not null
                        ? "paired-frame"
                        : "unpaired";
                    if (obicoReady)
                    {
                        FailureDetectionProviderStatusDto status = await AnalyzeAndHandleProviderAsync(
                            scope.ServiceProvider,
                            "Obico",
                            new FailureDetectionProviderRequest(
                                imageData,
                                obicoServerUrl!,
                                obicoApiKey,
                                SnapshotUrl: snapshotUrl),
                            currentSettings.AnalysisTimeoutSeconds,
                            snapshotUrl,
                            currentSettings.ConfidenceThreshold,
                            pairedAnalysisMode,
                            printer,
                            dbContext,
                            incidentHistoryService,
                            cancellationToken);
                        analyzedProviders.Add(status);
                        if (status.AnalysisMode != "paired-frame")
                        {
                            pairedAnalysisMode = "unpaired";
                        }

                        if (status.LastOutcome == AnalysisOutcomeFailure)
                        {
                            failuresDetected++;
                        }
                    }

                    if (printGuardReady && imageData is not null && printGuardEndpoint is not null)
                    {
                        FailureDetectionProviderStatusDto status = await AnalyzeAndHandleProviderAsync(
                            scope.ServiceProvider,
                            "PrintGuard",
                            new FailureDetectionProviderRequest(imageData, printGuardEndpoint.AbsoluteUri, printGuardSettings.ApiToken),
                            printGuardSettings.RequestTimeoutSeconds,
                            snapshotUrl,
                            currentSettings.ConfidenceThreshold,
                            pairedAnalysisMode,
                            printer,
                            dbContext,
                            incidentHistoryService,
                            cancellationToken);
                        analyzedProviders.Add(status);
                        if (status.LastOutcome == AnalysisOutcomeFailure)
                        {
                            failuresDetected++;
                        }
                    }

                    providerStatuses = providerStatuses
                        .Select(configured =>
                        {
                            FailureDetectionProviderStatusDto? analyzed = analyzedProviders.FirstOrDefault(candidate =>
                                string.Equals(configured.Provider, candidate.Provider, StringComparison.OrdinalIgnoreCase));
                            return analyzed is null
                                ? configured
                                : analyzed with
                                {
                                    LastConfidence = analyzed.LastConfidence ?? configured.LastConfidence,
                                    LastFailureDetectedAt = analyzed.LastFailureDetectedAt ?? configured.LastFailureDetectedAt,
                                };
                        })
                        .ToList();
                    FailureDetectionProviderStatusDto[] finalProviderStatuses = providerStatuses.ToArray();
                    bool anyFailure = analyzedProviders.Any(status => status.LastOutcome == AnalysisOutcomeFailure);
                    bool anyError = analyzedProviders.Any(status => status.LastOutcome == AnalysisOutcomeError)
                        || finalProviderStatuses.Any(status => status.State == PrinterStateError);
                    bool analyzedSameFrame = analyzedProviders.Count == 2
                        && analyzedProviders.All(status => status.AnalysisMode == "paired-frame");
                    FailureDetectionProviderStatusDto? lastAnalysis = analyzedProviders
                        .Where(status => status.LastAnalyzedAt.HasValue)
                        .OrderByDescending(status => status.LastAnalyzedAt)
                        .FirstOrDefault();
                    FailureDetectionProviderStatusDto? lastFailure = analyzedProviders
                        .Where(status => status.LastOutcome == AnalysisOutcomeFailure)
                        .OrderByDescending(status => status.LastAnalyzedAt)
                        .FirstOrDefault();
                    printerStatuses.Add(baseStatus with
                    {
                        State = anyError ? PrinterStateError : PrinterStateMonitoring,
                        Reason = anyFailure
                            ? "A provider detected a possible print failure; inspect the print."
                            : anyError
                                ? "One or more provider analyses failed; see provider details."
                                : analyzedSameFrame
                                    ? "Obico and PrintGuard analyzed the same captured frame."
                                    : analyzedProviders.Count > 0
                                        ? $"Monitoring via {string.Join(" and ", analyzedProviders.Select(status => status.Provider))}."
                                        : "No provider analysis completed.",
                        LastAnalyzedAt = lastAnalysis?.LastAnalyzedAt,
                        LastOutcome = anyFailure
                            ? AnalysisOutcomeFailure
                            : anyError
                                ? AnalysisOutcomeError
                                : lastAnalysis?.LastOutcome ?? AnalysisOutcomeNone,
                        LastConfidence = anyFailure ? lastFailure?.LastConfidence : lastAnalysis?.LastConfidence,
                        LastFailureDetectedAt = finalProviderStatuses
                            .Where(status => status.LastFailureDetectedAt.HasValue)
                            .Select(status => status.LastFailureDetectedAt)
                            .Max(),
                        Providers = finalProviderStatuses,
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[PrintFailureMonitor] Error analyzing printer {PrinterId} ({PrinterName})",
                        printer.Id,
                        printer.Name);
                    FailureDetectionPrinterStatusDto failedStatus = fallbackStatus ?? new FailureDetectionPrinterStatusDto
                    {
                        PrinterId = printer.Id,
                        PrinterName = printer.Name,
                    };
                    printerStatuses.Add(failedStatus with
                    {
                        State = PrinterStateError,
                        Reason = "Monitoring request failed.",
                        LastOutcome = AnalysisOutcomeError,
                        Providers = failedStatus.Providers
                            .Select(provider => provider.State == "ready"
                                ? provider with { State = PrinterStateError, Reason = "Monitoring request failed.", LastOutcome = AnalysisOutcomeError }
                                : provider)
                            .ToArray(),
                    });
                }
            }

            stopwatch.Stop();
            _metrics.RecordCycle(stopwatch.Elapsed.TotalMilliseconds, activeCount, configuredPrinters.Count);
            _monitorStatus.UpdateSnapshot(CreateSnapshot(
                currentSettings,
                currentSettings.Enabled || printGuardSettings.Enabled,
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
                MonitoringEnabled = currentSettings.Enabled || GetCurrentPrintGuardSettings().Enabled,
                ConfidenceThreshold = currentSettings.ConfidenceThreshold,
                ScanIntervalSeconds = currentSettings.ScanIntervalSeconds,
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

        static FailureDetectionProviderStatusDto CreateInitialProviderStatus(
            string provider,
            bool ready,
            string reason,
            FailureDetectionPrinterStatusDto? previousStatus,
            string defaultOutcome)
        {
            FailureDetectionProviderStatusDto? previousProviderStatus = previousStatus?.Providers
                .FirstOrDefault(status => string.Equals(status.Provider, provider, StringComparison.OrdinalIgnoreCase));
            return new FailureDetectionProviderStatusDto
            {
                Provider = provider,
                State = ready ? "ready" : PrinterStateDisabled,
                Reason = reason,
                LastAnalyzedAt = previousProviderStatus?.LastAnalyzedAt,
                LastOutcome = previousProviderStatus?.LastOutcome ?? defaultOutcome,
                LastConfidence = previousProviderStatus?.LastConfidence,
                LastFailureDetectedAt = previousProviderStatus?.LastFailureDetectedAt,
                AnalysisMode = previousProviderStatus?.AnalysisMode ?? "unpaired",
            };
        }

        async Task<FailureDetectionProviderResult> AnalyzeProviderAsync(
            IFailureDetectionProvider provider,
            FailureDetectionProviderRequest request,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                return await provider.AnalyzeAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new FailureDetectionProviderResult(0m, null, DateTime.UtcNow, $"{provider.ProviderName} analysis timed out.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PrintFailureMonitor] {Provider} analysis failed", provider.ProviderName);
                return new FailureDetectionProviderResult(0m, null, DateTime.UtcNow, $"{provider.ProviderName} analysis failed.");
            }
        }

        async Task<FailureDetectionProviderStatusDto> AnalyzeAndHandleProviderAsync(
            IServiceProvider providerServices,
            string providerName,
            FailureDetectionProviderRequest request,
            int timeoutSeconds,
            string snapshotUrl,
            decimal confidenceThreshold,
            string analysisMode,
            Printer printer,
            AppDbContext dbContext,
            IFailureDetectionIncidentHistoryService incidentHistoryService,
            CancellationToken providerCancellationToken)
        {
            var analysisStopwatch = Stopwatch.StartNew();
            try
            {
                IFailureDetectionProvider provider = providerServices
                    .GetRequiredKeyedService<IFailureDetectionProvider>(providerName);
                FailureDetectionProviderResult result = await AnalyzeProviderAsync(
                    provider,
                    request,
                    timeoutSeconds,
                    providerCancellationToken);
                if (result.UsedCapturedImage == false)
                {
                    analysisMode = "unpaired";
                }

                analysisStopwatch.Stop();
                return await HandleProviderResultAsync(
                    printer,
                    provider.ProviderName,
                    result,
                    snapshotUrl,
                    confidenceThreshold,
                    analysisMode,
                    analysisStopwatch.Elapsed.TotalMilliseconds,
                    dbContext,
                    incidentHistoryService,
                    providerCancellationToken);
            }
            catch (OperationCanceledException) when (providerCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                analysisStopwatch.Stop();
                _metrics.RecordError();
                _logger.LogWarning(
                    ex,
                    "[PrintFailureMonitor] {Provider} result handling failed for printer {PrinterId}",
                    providerName,
                    printer.Id);
                return new FailureDetectionProviderStatusDto
                {
                    Provider = providerName,
                    State = PrinterStateError,
                    Reason = $"{providerName} result handling failed.",
                    LastAnalyzedAt = DateTime.UtcNow,
                    LastOutcome = AnalysisOutcomeError,
                    AnalysisMode = analysisMode,
                };
            }
        }

        async Task<FailureDetectionProviderStatusDto> HandleProviderResultAsync(
            Printer printer,
            string provider,
            FailureDetectionProviderResult result,
            string snapshotUrl,
            decimal confidenceThreshold,
            string analysisMode,
            double durationMs,
            AppDbContext dbContext,
            IFailureDetectionIncidentHistoryService incidentHistoryService,
            CancellationToken cancellationToken)
        {
            if (result.ErrorMessage is not null)
            {
                _metrics.RecordError();
                return new FailureDetectionProviderStatusDto
                {
                    Provider = provider,
                    State = PrinterStateError,
                    Reason = result.ErrorMessage,
                    LastAnalyzedAt = result.AnalyzedAt,
                    LastOutcome = AnalysisOutcomeError,
                    AnalysisMode = "unpaired",
                };
            }

            bool failureDetected = result.IsFailureDetected ?? result.Score >= confidenceThreshold;
            _metrics.RecordAnalysis(durationMs, (double)result.Score, failureDetected);
            if (failureDetected)
            {
                FailureDetectionResult failureResult = new()
                {
                    IsFailureDetected = true,
                    Confidence = result.Score,
                    AnalyzedAt = result.AnalyzedAt,
                };
                await HandleFailureDetectedAsync(
                    printer,
                    provider,
                    snapshotUrl,
                    failureResult,
                    dbContext,
                    incidentHistoryService,
                    cancellationToken);
            }

            return new FailureDetectionProviderStatusDto
            {
                Provider = provider,
                State = PrinterStateMonitoring,
                Reason = failureDetected ? "Failure detected; inspect the print." : string.Empty,
                LastAnalyzedAt = result.AnalyzedAt,
                LastOutcome = failureDetected ? AnalysisOutcomeFailure : AnalysisOutcomeHealthy,
                LastConfidence = result.Score,
                LastFailureDetectedAt = failureDetected ? result.AnalyzedAt : null,
                AnalysisMode = analysisMode,
            };
        }
    }

    private FailureDetectionMonitorStatusDto CreateSnapshot(
        ObicoSettings currentSettings,
        bool monitoringEnabled,
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
            MonitoringEnabled = monitoringEnabled,
            ConfidenceThreshold = currentSettings.ConfidenceThreshold,
            ScanIntervalSeconds = currentSettings.ScanIntervalSeconds,
            ConfiguredPrinterCount = configuredPrinterCount,
            ActivelyMonitoredPrinterCount = activelyMonitoredPrinterCount,
            LastAnalyzedPrinterCount = lastAnalyzedPrinterCount,
            LastFailureCount = lastFailureCount,
            LastScanStartedAt = lastScanStartedAt,
            LastScanCompletedAt = lastScanCompletedAt,
            LastError = lastError,
            Printers = printers,
        };

    private static (string DetectionSource, string? DetectionTarget) ResolveProviderTargets(
        bool obicoSelected,
        bool printGuardSelected,
        string obicoDetectionSource,
        string? obicoDetectionTarget)
    {
        if (obicoSelected && printGuardSelected)
        {
            string obicoTarget = obicoDetectionTarget ?? "Obico (not configured)";
            return ("multiple", $"{obicoTarget} + PrintGuard");
        }

        if (printGuardSelected)
        {
            return ("printguard", "PrintGuard");
        }

        return obicoSelected
            ? (obicoDetectionSource, obicoDetectionTarget)
            : (DetectionSourceNone, null);
    }

    private async Task HandleFailureDetectedAsync(
        Printer printer,
        string provider,
        string? snapshotUrl,
        FailureDetectionResult result,
        AppDbContext dbContext,
        IFailureDetectionIncidentHistoryService incidentHistoryService,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "[PrintFailureMonitor] Failure detected for printer {PrinterId} ({PrinterName}): confidence={Confidence:F3}",
            printer.Id,
            printer.Name,
            result.Confidence);

        PrintJob? currentJob = await FindActiveJobAsync(
            dbContext,
            printer.Id,
            cancellationToken);
        var jobContext = ResolveJobContext(_statusCache.GetStatus(printer.Id), currentJob?.Name);

        // Publish failure event to SignalR clients
        var failureEvent = new FailureDetectionDto
        {
            PrinterId = printer.Id,
            Provider = provider,
            PrinterName = printer.Name,
            JobId = currentJob?.Id,
            JobName = jobContext.JobName,
            FileName = jobContext.FileName,
            Confidence = result.Confidence,
            DetectedAt = result.AnalyzedAt,
            SnapshotUrl = snapshotUrl,
            AutoPaused = false
        };

        try
        {
            FailureDetectionIncident recordedIncident = await incidentHistoryService.RecordFailureAsync(
                printer.Id,
                currentJob?.Id,
                jobContext.JobName,
                jobContext.FileName,
                result.Confidence,
                result.AnalyzedAt,
                snapshotUrl,
                provider,
                failureEvent.AutoPaused,
                cancellationToken);
            failureEvent.Id = recordedIncident.Id;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(
                ex,
                "[PrintFailureMonitor] Failed to persist incident history for printer {PrinterId}, job {JobId}",
                printer.Id,
                currentJob?.Id);
        }

        // Broadcast failure event to all connected clients
        await _hub.Clients.Group(AuthorizedHubGroups.Farm)
            .SendAsync("FailureDetected", failureEvent, cancellationToken);

        _logger.LogInformation(
            "[PrintFailureMonitor] Failure event broadcast for printer {PrinterId}, job {JobId}",
            printer.Id,
            currentJob?.Id);
    }

    internal static Task<PrintJob?> FindActiveJobAsync(
        AppDbContext dbContext,
        Guid printerId,
        CancellationToken cancellationToken = default) =>
        dbContext.PrintJobs
            .Where(j =>
                j.AssignedPrinterId == printerId
                && (j.Status == PrintJobStatus.Printing
                    || j.Status == PrintJobStatus.Starting))
            .OrderByDescending(j => j.ActualStartTime ?? j.QueuedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private ObicoSettings GetCurrentSettings()
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        return settingsService.Get<ObicoSettings>();
    }

    private PrintGuardShadowSettings GetCurrentPrintGuardSettings()
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IOptionsMonitor<PrintGuardShadowSettings>>().CurrentValue;
    }
}
