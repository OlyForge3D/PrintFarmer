namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service responsible for artifact cleanup based on retention policy.
/// </summary>
public interface IArtifactCleanupService
{
    /// <summary>
    /// Scan for artifacts eligible for cleanup based on age and size thresholds.
    /// In dry-run mode, only logs candidates without deleting.
    /// Returns the number of artifacts that would be (or were) deleted.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> ScanAndCleanupAsync(CancellationToken ct);
}
