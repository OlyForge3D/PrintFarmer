using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Thread-safe, in-memory, last-known-good cache of independently verified release evidence
/// (issue #2757). Populated exclusively by <see cref="VerifiedReleaseDiscoveryMonitorService"/>
/// after a successful signed-release discovery + Cosign verification round; read by
/// <c>SystemInfoService</c> to feed <c>ReleaseReadinessEvaluator.Evaluate</c>.
/// <para>
/// This cache ONLY ever holds discovery/verification metadata. It never triggers, stages, or
/// tracks any update application — see <see cref="VerifiedReleaseDiscoveryMonitorService"/>'s
/// class remarks for the discover/cache-only scope boundary.
/// </para>
/// <para>
/// A discovery failure (network error, verification failure, missing/invalid manifest) records
/// <see cref="LastError"/> via <see cref="SetError"/> but deliberately leaves the previously
/// cached <see cref="Current"/>/<see cref="LastVerifiedAt"/> untouched — a transient discovery
/// failure must not regress readiness evaluation back to "no evidence" (which
/// <c>ReleaseReadinessEvaluator</c> treats as <c>NotManaged</c>) when a perfectly good previous
/// verification is still the best available evidence. This is retaining safe previous state,
/// not a success-shaped fallback: <see cref="LastError"/> remains visible/logged until the next
/// successful discovery clears it.
/// </para>
/// </summary>
public interface IVerifiedReleaseEvidenceCache
{
    /// <summary>The most recently verified evidence, or <c>null</c> if discovery has never
    /// succeeded since process start.</summary>
    VerifiedReleaseEvidenceDto? Current { get; }

    /// <summary>UTC timestamp of the last successful verification, or <c>null</c> if none yet.</summary>
    DateTimeOffset? LastVerifiedAt { get; }

    /// <summary>The most recent discovery failure message, or <c>null</c> if the most recent
    /// discovery attempt (if any) succeeded.</summary>
    string? LastError { get; }

    /// <summary>Records a freshly verified release as the new current evidence and clears
    /// <see cref="LastError"/>.</summary>
    void SetVerified(VerifiedReleaseEvidenceDto evidence, DateTimeOffset verifiedAt);

    /// <summary>Records a discovery failure. <see cref="Current"/>/<see cref="LastVerifiedAt"/>
    /// are deliberately left unchanged — see class remarks.</summary>
    void SetError(string error);
}

/// <inheritdoc cref="IVerifiedReleaseEvidenceCache"/>
public sealed class VerifiedReleaseEvidenceCache : IVerifiedReleaseEvidenceCache
{
    private readonly Lock _gate = new();
    private VerifiedReleaseEvidenceDto? _current;
    private DateTimeOffset? _lastVerifiedAt;
    private string? _lastError;

    /// <inheritdoc/>
    public VerifiedReleaseEvidenceDto? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? LastVerifiedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastVerifiedAt;
            }
        }
    }

    /// <inheritdoc/>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <inheritdoc/>
    public void SetVerified(VerifiedReleaseEvidenceDto evidence, DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            _current = evidence;
            _lastVerifiedAt = verifiedAt;
            _lastError = null;
        }
    }

    /// <inheritdoc/>
    public void SetError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        lock (_gate)
        {
            // Deliberately does NOT touch _current/_lastVerifiedAt. See interface remarks.
            _lastError = error;
        }
    }
}
