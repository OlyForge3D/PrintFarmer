namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Service for capturing camera snapshots on print events and storing them
/// in the filesystem tied to print job history.
/// </summary>
public interface ICameraSnapshotService
{
    /// <summary>
    /// Captures snapshots from all cameras associated with a printer.
    /// Fetches JPEG from each camera's SnapshotUrl, stores to filesystem,
    /// and creates tracking records in the database.
    /// </summary>
    /// <param name="printerId">The printer ID whose cameras should capture snapshots.</param>
    /// <param name="eventType">The print event type (e.g., PrintStarted, PrintCompleted, PrintFailed).</param>
    /// <param name="printJobId">Optional print job ID to associate with the snapshot.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CaptureSnapshotAsync(Guid printerId, string eventType, Guid? printJobId = null, CancellationToken ct = default);
}
