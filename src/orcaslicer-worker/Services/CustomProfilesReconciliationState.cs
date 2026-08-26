namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Tracks whether this worker's overlay and process caches match the shared
/// custom profile volume.
/// </summary>
public sealed class CustomProfilesReconciliationState
{
    private readonly object _sync = new();
    private string? _appliedFingerprint;
    private string? _failure;
    private int _isReady;

    /// <summary>
    /// Gets whether this worker may claim new slicing jobs.
    /// </summary>
    public bool IsReady => Volatile.Read(ref _isReady) != 0;

    /// <summary>
    /// Gets the fingerprint currently loaded by this worker.
    /// </summary>
    public string? AppliedFingerprint
    {
        get
        {
            lock (_sync)
            {
                return _appliedFingerprint;
            }
        }
    }

    /// <summary>
    /// Gets the latest reconciliation failure.
    /// </summary>
    public string? Failure
    {
        get
        {
            lock (_sync)
            {
                return _failure;
            }
        }
    }

    /// <summary>
    /// Marks the worker synchronized with the supplied fingerprint.
    /// </summary>
    /// <param name="fingerprint">Loaded shared-volume fingerprint.</param>
    public void MarkReady(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (_sync)
        {
            _appliedFingerprint = fingerprint;
            _failure = null;
            Volatile.Write(ref _isReady, 1);
        }
    }

    /// <summary>
    /// Prevents this worker from claiming jobs until reconciliation succeeds.
    /// </summary>
    /// <param name="failure">Visible failure reason.</param>
    public void MarkUnavailable(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        lock (_sync)
        {
            _failure = failure;
            Volatile.Write(ref _isReady, 0);
        }
    }
}
