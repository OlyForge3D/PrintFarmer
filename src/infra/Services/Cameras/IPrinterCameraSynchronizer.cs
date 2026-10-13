namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Discovers cameras reported by a printer's backend plugin and reconciles them with the
/// <see cref="Domain.Camera"/> table.
/// </summary>
public interface IPrinterCameraSynchronizer
{
    /// <summary>Synchronizes discovered cameras for a single printer.</summary>
    /// <param name="printerId">Printer to synchronize.</param>
    /// <param name="explicitRequest">
    /// True when an operator explicitly asked for discovery; vanished cameras are then marked
    /// offline even if the status cache does not report the printer online.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrinterCameraSyncResult> SyncPrinterAsync(Guid printerId, bool explicitRequest, CancellationToken ct);

    /// <summary>Synchronizes discovered cameras for every enabled printer.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<PrinterCameraSyncResult>> SyncAllAsync(CancellationToken ct);
}

/// <summary>Outcome of synchronizing one printer's cameras.</summary>
/// <param name="PrinterId">Printer that was synchronized.</param>
/// <param name="PrinterFound">False when the printer does not exist.</param>
/// <param name="DiscoverySucceeded">False when the backend discovery call failed.</param>
/// <param name="Created">Number of camera rows created.</param>
/// <param name="Updated">Number of camera rows or links updated.</param>
/// <param name="MarkedOffline">Number of cameras marked offline because the backend no longer reports them.</param>
/// <param name="CameraIds">Cameras linked to the printer after synchronization.</param>
public sealed record PrinterCameraSyncResult(
    Guid PrinterId,
    bool PrinterFound,
    bool DiscoverySucceeded,
    int Created,
    int Updated,
    int MarkedOffline,
    IReadOnlyList<Guid> CameraIds)
{
    /// <summary>True when the sync changed any persisted camera data.</summary>
    public bool Changed => Created > 0 || Updated > 0 || MarkedOffline > 0;

    /// <summary>Result for a printer that does not exist.</summary>
    public static PrinterCameraSyncResult NotFound(Guid printerId) =>
        new(printerId, false, false, 0, 0, 0, []);
}

/// <summary>Payload of the lowercase <c>camerasupdated</c> SignalR event.</summary>
/// <param name="PrinterId">Printer whose cameras changed.</param>
/// <param name="CameraIds">Cameras linked to the printer after the change.</param>
public sealed record CamerasUpdatedEvent(Guid PrinterId, IReadOnlyList<Guid> CameraIds);

/// <summary>Requests an asynchronous camera sync for a printer (e.g. on connect or create).</summary>
public interface IPrinterCameraSyncTrigger
{
    /// <summary>Queues a background camera sync for the printer. Never blocks or throws.</summary>
    /// <param name="printerId">Printer to synchronize.</param>
    void RequestSync(Guid printerId);
}
