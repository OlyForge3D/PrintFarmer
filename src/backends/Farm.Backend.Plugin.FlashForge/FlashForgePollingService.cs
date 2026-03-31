using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// Background service for polling FlashForge printers and broadcasting status updates via SignalR.
/// FlashForge uses a proprietary TCP serial protocol, so status must be polled periodically.
/// </summary>
public sealed class FlashForgePollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<FlashForgePollingService> logger,
    IPrinterStatusCacheWriter statusCacheWriter) : IHostedService, IDisposable
{
    private readonly ILogger<FlashForgePollingService> _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();

    /// <summary>
    /// Polling interval for FlashForge printers.
    /// Slightly longer than PrusaLink (5s) because each poll opens 4 sequential TCP connections.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each FlashForge printer to track changes and avoid redundant updates.
    /// </summary>
    private sealed class PrinterPollingState
    {
        public Guid PrinterId { get; set; }

        public string? LastKnownState { get; set; }

        /// <summary>
        /// Previous state before the last update, used for detecting print completion transitions.
        /// </summary>
        public string? PreviousState { get; set; }

        public double? LastKnownProgress { get; set; }

        public string? LastKnownJobName { get; set; }

        public bool LastKnownIsOnline { get; set; }

        public DateTime LastPollTime { get; set; }

        public int ConsecutiveFailures { get; set; }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FlashForgePollingService starting");
#pragma warning disable VSTHRD003 // Avoid awaiting or returning a Task representing work that was not started within this context
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FlashForgePollingService stopping");
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed – safe to ignore during shutdown
        }

        try
        {
            if (_mainLoop is not null)
            {
#pragma warning disable VSTHRD003 // Task not started in async context is expected here
                await _mainLoop;
#pragma warning restore VSTHRD003
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when service is stopped
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
        _pollingLoops.Clear();
    }

    /// <summary>
    /// Main loop that continuously monitors and polls all FlashForge printers.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("FlashForgePollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    List<Guid> printerIds = await GetFlashForgePrinterIdsAsync(ct);
                    _logger.LogDebug("FlashForgePollingService: Found {Count} FlashForge printers", printerIds.Count);

                    // Ensure polling loops exist for all FlashForge printers
                    foreach (Guid id in printerIds)
                    {
                        if (!_pollingLoops.ContainsKey(id))
                        {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                            var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                            _pollingLoops.TryAdd(id, pollingLoop);
                            _logger.LogDebug("Started polling loop for FlashForge printer {Id}", id);
                        }
                    }

                    // Remove polling loops for printers that are no longer FlashForge
                    var inactiveIds = _pollingLoops.Keys.Except(printerIds).ToList();
                    foreach (Guid printerId in inactiveIds)
                    {
                        _pollingLoops.TryRemove(printerId, out _);
                        _printerStates.TryRemove(printerId, out _);
                        _logger.LogDebug("Stopped polling for FlashForge printer {PrinterId}", printerId);
                    }

                    // Check every 30 seconds for printer list changes
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in FlashForgePollingService main loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FlashForgePollingService main loop cancelled");
        }
    }

    /// <summary>
    /// Polls a single FlashForge printer at regular intervals.
    /// </summary>
    private async Task PollPrinterAsync(Guid printerId, CancellationToken ct)
    {
#pragma warning disable S6612 // Capturing printerId in lambda is intentional and safe
        PrinterPollingState state = _printerStates.GetOrAdd(printerId, _ => new PrinterPollingState { PrinterId = printerId, LastKnownIsOnline = false });
#pragma warning restore S6612

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Printer? printer = await GetPrinterAsync(printerId, ct);
                if (printer?.Backend != (int)PrinterBackend.FlashForge)
                {
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    return;
                }

                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    IFlashForgeClient flashForgeClient = scope.ServiceProvider.GetRequiredService<IFlashForgeClient>();
                    ManagedSpoolProviderHelper spoolProvider = scope.ServiceProvider.GetRequiredService<ManagedSpoolProviderHelper>();

                    PrinterCompositeStatus status = await flashForgeClient.GetCompositeStatusAsync(
                        printer.ServerUrl,
                        ct);

                    _logger.LogDebug(
                        "FlashForge {PrinterId}: Online={IsOnline}, State={State}, Progress={Progress}, HotendTemp={HotendTemp}",
                        printerId, status.IsOnline, status.State, status.Progress, status.HotendTemp);

                    // Track state transitions for job completion detection
                    string? previousState = state.LastKnownState;
                    bool stateChanged = status.State != previousState;

                    // Update state tracking
                    state.PreviousState = previousState;
                    state.LastKnownIsOnline = status.IsOnline;
                    state.LastKnownState = status.State;
                    state.LastKnownProgress = status.Progress;
                    state.LastKnownJobName = status.JobName;
                    state.ConsecutiveFailures = 0;

                    // Check for print completion/failure transitions
                    if (stateChanged && previousState != null)
                    {
                        await CheckAndSyncJobCompletionAsync(printerId, previousState, status.State!, ct);
                    }

                    // Resolve spool info from DB assignment
                    PrinterSpoolInfoDto? spoolInfo = await spoolProvider.GetManagedSpoolInfoAsync(printer, ct);

                    var update = new PrinterStatusDto(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        CameraSnapshotUrl: status.CameraSnapshotUrl,
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        SpoolInfo: spoolInfo,
                        ExtruderTemperatures: status.ExtruderTemperatures,
                        DetectedExtruderCount: status.DetectedExtruderCount);

                    _statusCacheWriter.UpdateStatus(update);

                    await _hub.Clients.All.SendAsync("printerupdated", update.WithNormalizedFileName(), ct);

                    state.LastPollTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    state.ConsecutiveFailures++;
                    _logger.LogDebug(ex, "Failed to poll FlashForge printer {PrinterId} (attempt {Attempts})", printerId, state.ConsecutiveFailures);

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning("FlashForge printer {PrinterId} marked offline after {Attempts} failures", printerId, state.ConsecutiveFailures);
                        state.LastKnownIsOnline = false;
                        var offlineUpdate = new PrinterStatusDto(
                            Id: printerId,
                            IsOnline: false,
                            State: null,
                            Progress: null,
                            JobName: null,
                            ThumbnailUrl: null,
                            CameraStreamUrl: null,
                            CameraSnapshotUrl: null,
                            X: null,
                            Y: null,
                            Z: null,
                            HotendTemp: null,
                            BedTemp: null,
                            HotendTarget: null,
                            BedTarget: null,
                            SpoolInfo: null);

                        _statusCacheWriter.UpdateStatus(offlineUpdate);

                        await _hub.Clients.All.SendAsync("printerupdated", offlineUpdate, ct);
                    }
                }

                state.LastPollTime = DateTime.UtcNow;
                await Task.Delay(PollingInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error polling FlashForge printer {PrinterId}", printerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Gets the list of all FlashForge printer IDs from the database.
    /// </summary>
    private async Task<List<Guid>> GetFlashForgePrinterIdsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        List<Printer> printers = await unitOfWork.Printers.GetByBackendAsync(PrinterBackend.FlashForge, ct);
        return printers.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// Gets a printer by ID from the database.
    /// </summary>
    private async Task<Printer?> GetPrinterAsync(Guid printerId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.Printers.FindByIdAsync(printerId, ct);
    }

    /// <summary>
    /// Checks for print completion/failure state transitions and synchronizes job status in database.
    /// </summary>
    private async Task CheckAndSyncJobCompletionAsync(Guid printerId, string previousState, string newState, CancellationToken ct)
    {
        try
        {
            if (!PrintJobCompletionService.IsPrintingState(previousState))
            {
                return;
            }

            _logger.LogInformation("[FlashForgePollingService] State transition for printer {PrinterId}: {Previous} -> {New}", printerId, previousState, newState);

            using IServiceScope scope = _scopeFactory.CreateScope();
            IPrintJobCompletionService completionService = scope.ServiceProvider.GetRequiredService<IPrintJobCompletionService>();

            if (PrintJobCompletionService.IsCompletionState(newState))
            {
                bool marked = await completionService.MarkCurrentJobAsCompletedAsync(printerId, newState, ct);
                if (marked)
                {
                    _logger.LogInformation("[FlashForgePollingService] Print job marked as completed for printer {PrinterId}", printerId);
                }
            }
            else if (PrintJobCompletionService.IsFailureState(newState))
            {
                bool marked = await completionService.MarkCurrentJobAsFailedAsync(printerId, $"Printer state changed to {newState}", ct);
                if (marked)
                {
                    _logger.LogWarning("[FlashForgePollingService] Print job marked as failed for printer {PrinterId} (state: {NewState})", printerId, newState);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FlashForgePollingService] Failed to sync job completion for printer {PrinterId}", printerId);
        }
    }
}
