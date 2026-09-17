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
/// cached <see cref="Current"/>/<see cref="LastVerifiedAt"/> untouched so diagnostics retain the
/// last independently verified target. Readiness does not treat that retained target as
/// indefinitely eligible: <c>SystemInfoService</c> emits a non-success state whenever discovery
/// is disabled, the latest round failed, or verification is older than twice the configured
/// polling interval. <see cref="LastError"/> remains visible until the next successful discovery.
/// </para>
/// <para>
/// <b>Monotonicity is scoped to the evidence's identity channel (stable vs. insider), not global.</b>
/// The two channels are distinct release topologies with independent sequence numbering (the
/// insider suffix and the stable suffix are disjoint ranges — see
/// <c>SignedUpdateManifestValidator.DeriveSequence</c>), so an insider sequence is never
/// comparable to a stable sequence. <see cref="SetVerified"/> only rejects a lower sequence when
/// it shares <see cref="VerifiedReleaseEvidenceDto.Identity"/>'s <c>Channel</c> with the
/// currently cached evidence. When the channel differs — e.g. an operator switches the update
/// channel setting from insider back to stable — the newly discovered evidence always replaces
/// <see cref="Current"/> regardless of its <c>Sequence</c>, because that switch is an operator
/// decision to re-target a different release line, not a downgrade within one.
/// </para>
/// </summary>
public interface IVerifiedReleaseEvidenceCache
{
    /// <summary>Returns one immutable, point-in-time view of all cached evidence state.</summary>
    VerifiedReleaseEvidenceCacheSnapshot GetSnapshot();

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
    bool SetVerified(VerifiedReleaseEvidenceDto evidence, DateTimeOffset verifiedAt);

    /// <summary>Records a discovery failure. <see cref="Current"/>/<see cref="LastVerifiedAt"/>
    /// are deliberately left unchanged — see class remarks.</summary>
    void SetError(string error);
}

/// <summary>Atomic point-in-time view of verified release cache state.</summary>
public sealed record VerifiedReleaseEvidenceCacheSnapshot(
    VerifiedReleaseEvidenceDto? Current,
    DateTimeOffset? LastVerifiedAt,
    string? LastError);

/// <inheritdoc cref="IVerifiedReleaseEvidenceCache"/>
public sealed class VerifiedReleaseEvidenceCache : IVerifiedReleaseEvidenceCache
{
    private const string DefaultError = "Verified release discovery failed without an error message.";
    private const int MaximumErrorLength = 1024;
    private readonly Lock _gate = new();
    private VerifiedReleaseEvidenceDto? _current;
    private DateTimeOffset? _lastVerifiedAt;
    private string? _lastError;

    /// <inheritdoc/>
    public VerifiedReleaseEvidenceCacheSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new(_current, _lastVerifiedAt, _lastError);
        }
    }

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
    public bool SetVerified(VerifiedReleaseEvidenceDto evidence, DateTimeOffset verifiedAt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            // Rollback protection only applies within the same identity channel. Sequences from
            // different channels (stable vs. insider) are independent topologies and are never
            // comparable, so a channel switch always replaces the cached evidence — see class
            // remarks.
            bool sameChannel = _current is not null
                && string.Equals(_current.Identity?.Channel, evidence.Identity?.Channel, StringComparison.Ordinal);
            if (sameChannel && evidence.Sequence < _current!.Sequence)
            {
                return false;
            }

            _current = evidence;
            _lastVerifiedAt = verifiedAt;
            _lastError = null;
            return true;
        }
    }

    /// <inheritdoc/>
    public void SetError(string error)
    {
        string normalized = string.IsNullOrWhiteSpace(error) ? DefaultError : error.Trim();
        if (normalized.Length > MaximumErrorLength)
        {
            normalized = normalized[..MaximumErrorLength];
        }

        lock (_gate)
        {
            // Deliberately does NOT touch _current/_lastVerifiedAt. See interface remarks.
            _lastError = normalized;
        }
    }
}
