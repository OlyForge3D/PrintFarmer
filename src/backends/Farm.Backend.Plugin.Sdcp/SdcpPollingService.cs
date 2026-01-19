using System.Collections.Concurrent;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.Sdcp;

/// <summary>
/// Background service for polling SDCP printers and broadcasting status updates via SignalR.
/// SDCP printers use WebSocket-based communication but don't support real-time subscriptions,
/// so we poll them periodically for status updates.
/// </summary>
public sealed class SdcpPollingService(
    IHubContext<PrinterHub> hub,
    IServiceScopeFactory scopeFactory,
    IUnifiedLoggingService logger,
    IPrinterStatusCacheWriter statusCacheWriter) : IHostedService, IDisposable
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IHubContext<PrinterHub> _hub = hub;
    private readonly IPrinterStatusCacheWriter _statusCacheWriter = statusCacheWriter;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, PrinterPollingState> _printerStates = new();
    private readonly ConcurrentDictionary<Guid, Task> _pollingLoops = new();

    // Polling interval for SDCP printers (same as PrusaLink)
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    private Task? _mainLoop;

    /// <summary>
    /// Persistent state for each SDCP printer to track changes and avoid redundant updates.
    /// </summary>
    private sealed class PrinterPollingState
    {
        public Guid PrinterId { get; set; }

        public string? LastKnownState { get; set; }

        public double? LastKnownProgress { get; set; }

        public string? LastKnownJobName { get; set; }

        public bool LastKnownIsOnline { get; set; }

        public DateTime LastPollTime { get; set; }

        public int ConsecutiveFailures { get; set; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdcpPollingService starting");
#pragma warning disable VSTHRD003 // Avoid awaiting or returning a Task representing work that was not started within this context
        _ = _mainLoop = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
#pragma warning restore VSTHRD003
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SdcpPollingService stopping");
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

    public void Dispose()
    {
        _cts?.Dispose();
        _pollingLoops.Clear();
    }

    /// <summary>
    /// Main loop that continuously monitors and polls all SDCP printers.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("SdcpPollingService main loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Get list of SDCP printers from database
                    List<Guid> printerIds = await GetSdcpPrinterIdsAsync(ct);
                    _logger.LogDebug($"SdcpPollingService: Found {printerIds.Count} SDCP printers");

                    // Ensure polling loops exist for all SDCP printers
                    foreach (Guid id in printerIds)
                    {
                        if (!_pollingLoops.ContainsKey(id))
                        {
#pragma warning disable S6612 // Use the loop variable instead of capturing
                            var pollingLoop = Task.Run(() => PollPrinterAsync(id, ct), ct);
#pragma warning restore S6612
                            _pollingLoops.TryAdd(id, pollingLoop);
                            _logger.LogDebug($"Started polling loop for SDCP printer {id}");
                        }
                    }

                    // Remove polling loops for printers that are no longer SDCP
                    var inactiveIds = _pollingLoops.Keys.Except(printerIds).ToList();
                    foreach (Guid printerId in inactiveIds)
                    {
                        _pollingLoops.TryRemove(printerId, out _);
                        _printerStates.TryRemove(printerId, out _);
                        _logger.LogDebug($"Stopped polling for printer {printerId}");
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
                    _logger.LogError(ex, "Error in SdcpPollingService main loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SdcpPollingService main loop cancelled");
        }
    }

    /// <summary>
    /// Polls a single SDCP printer at regular intervals.
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
                // Get printer details
                Printer? printer = await GetPrinterAsync(printerId, ct);
                if (printer?.Backend != (int)PrinterBackend.SDCP)
                {
                    // Printer is no longer SDCP, remove from polling
                    _pollingLoops.TryRemove(printerId, out _);
                    _printerStates.TryRemove(printerId, out _);
                    return;
                }

                // Poll the printer
                try
                {
                    // Get the SDCP client from a scoped context
                    // (scoped services cannot be injected directly into singletons)
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    ISdcpClient sdcpClient = scope.ServiceProvider.GetRequiredService<ISdcpClient>();

                    // SDCP uses BackendUrl which combines ServerUrl with BackendPort
                    // The GetCompositeStatusAsync expects the base URL (which it converts to WebSocket URL internally)
                    PrinterCompositeStatus status = await sdcpClient.GetCompositeStatusAsync(
                        printer.BackendUrl,
                        ct);

                    _logger.LogDebug($"SDCP {printerId}: Got status - Online={status.IsOnline}, State={status.State}, Progress={status.Progress}, JobName={status.JobName}");

                    // Check if any values changed
                    // Use tolerance for float progress comparison
#pragma warning disable S1244 // Using explicit tolerance (0.01) for float comparison is appropriate here
                    bool progressChanged = state.LastKnownProgress == null || status.Progress == null
                        ? state.LastKnownProgress != status.Progress
                        : Math.Abs(state.LastKnownProgress.Value - status.Progress.Value) > 0.01;
#pragma warning restore S1244
                    bool hasChanges = state.LastKnownIsOnline != status.IsOnline
                        || state.LastKnownState != status.State
                        || progressChanged
                        || state.LastKnownJobName != status.JobName;

                    state.LastKnownIsOnline = status.IsOnline;
                    state.LastKnownState = status.State;
                    state.LastKnownProgress = status.Progress;
                    state.LastKnownJobName = status.JobName;
                    state.ConsecutiveFailures = 0;

                    // Broadcast update via SignalR using PrinterStatusDto
                    var update = new PrinterStatusDto(
                        Id: printerId,
                        IsOnline: status.IsOnline,
                        State: PrinterStateNormalizer.NormalizeState(status.State),
                        Progress: status.Progress,
                        JobName: status.JobName,
                        ThumbnailUrl: status.ThumbnailUrl,
                        CameraStreamUrl: status.CameraStreamUrl,
                        CameraSnapshotUrl: null,
                        X: status.X,
                        Y: status.Y,
                        Z: status.Z,
                        HotendTemp: status.HotendTemp,
                        BedTemp: status.BedTemp,
                        HotendTarget: status.HotendTarget,
                        BedTarget: status.BedTarget,
                        SpoolInfo: null);

                    // Update cache before broadcasting to clients
                    _statusCacheWriter.UpdateStatus(update);

                    await _hub.Clients.All.SendAsync("printerupdated", update, ct);

                    state.LastPollTime = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    state.ConsecutiveFailures++;
                    _logger.LogDebug(ex, $"Failed to poll SDCP printer {printerId} (attempt {state.ConsecutiveFailures})");

                    // After 3 consecutive failures, mark as offline
                    if (state.ConsecutiveFailures >= 3 && state.LastKnownIsOnline)
                    {
                        _logger.LogWarning($"SDCP printer {printerId} marked offline after {state.ConsecutiveFailures} failures");
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

                        // Update cache before broadcasting to clients
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
                _logger.LogError(ex, $"Unexpected error polling SDCP printer {printerId}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>
    /// Gets the list of all SDCP printer IDs from the database.
    /// </summary>
    private async Task<List<Guid>> GetSdcpPrinterIdsAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        List<Printer> printers = await unitOfWork.Printers.GetByBackendAsync(PrinterBackend.SDCP, ct);
        return printers.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// Gets a printer by ID from the database.
    /// </summary>
    private async Task<Printer?> GetPrinterAsync(Guid printerId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.UnitOfWork.IUnitOfWork>();
        return await unitOfWork.Printers.FindByIdAsync(printerId, ct);
    }
}
