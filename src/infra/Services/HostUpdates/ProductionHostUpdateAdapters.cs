using System.Runtime.InteropServices;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>Readiness supplied by concrete host installation and recovery facilities.</summary>
public interface IHostUpdateCandidateReadiness
{
    bool CompatibilityReady { get; }

    bool InstallationAvailable { get; }

    bool SafetyPassed { get; }

    bool IsNewer { get; }
}

/// <summary>Fail-closed until host installation and recovery are implemented.</summary>
public sealed class UnavailableHostUpdateCandidateReadiness : IHostUpdateCandidateReadiness
{
    public bool CompatibilityReady => false;

    public bool InstallationAvailable => false;

    public bool SafetyPassed => false;

    public bool IsNewer => false;
}

/// <summary>
/// Converts only independently verified release evidence into scheduler candidates. Inventory and
/// self-reported service observations are deliberately not inputs to this adapter.
/// </summary>
public sealed class VerifiedReleaseEvidenceCandidateCache(
    IVerifiedReleaseEvidenceCache evidence,
    IHostUpdateCandidateReadiness readiness,
    string hostPlatform,
    TimeSpan maximumFreshness) : IHostUpdateSchedulerCandidateCache
{
    private static readonly HashSet<string> RequiredServices =
        ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];

    public VerifiedHostUpdateCandidate? Current => TryMap(out _);

    public string? LastError
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(evidence.LastError))
            {
                return evidence.LastError;
            }

            _ = TryMap(out string? error);
            return error;
        }
    }

    private VerifiedHostUpdateCandidate? TryMap(out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(evidence.LastError))
        {
            error = evidence.LastError;
            return null;
        }

        VerifiedReleaseEvidenceDto? current = evidence.Current;
        DateTimeOffset? verifiedAt = evidence.LastVerifiedAt;
        if (current is null)
        {
            error = "verified_release_evidence_missing";
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (verifiedAt is null || verifiedAt > now || now - verifiedAt > maximumFreshness)
        {
            error = "verified_release_evidence_stale";
            return null;
        }

        if (!current.SignatureVerified || !current.IsComplete)
        {
            error = "verified_release_evidence_untrusted";
            return null;
        }

        CanonicalReleaseIdentityDto? identity = current.Identity;
        if (identity is null
            || string.IsNullOrWhiteSpace(identity.ReleaseId)
            || string.IsNullOrWhiteSpace(identity.Channel)
            || string.IsNullOrWhiteSpace(identity.SourceCommit)
            || (identity.Channel != UpdateChannelSettings.StableChannel && identity.Channel != UpdateChannelSettings.InsiderChannel)
            || !IsCommit(identity.SourceCommit)
            || !HostUpdateValidation.IsDigest(current.ManifestDigest))
        {
            error = "verified_release_identity_invalid";
            return null;
        }

        if (!SignedUpdateManifestValidator.IsPlatform(hostPlatform))
        {
            error = "host_platform_invalid";
            return null;
        }

        if (current.Services.Count != RequiredServices.Count
            || current.Services.Any(service => !RequiredServices.Contains(service.ServiceId))
            || current.Services.Select(service => service.ServiceId).Distinct(StringComparer.Ordinal).Count() != RequiredServices.Count)
        {
            error = "verified_release_target_set_invalid";
            return null;
        }

        if (current.Services.Any(service => !string.Equals(service.Platform, hostPlatform, StringComparison.Ordinal)
            || !HostUpdateValidation.IsDigest(service.PlatformDigest)
            || !HostUpdateValidation.IsDigest(service.IndexDigest)
            || (service.ServiceId == "orcaslicer-worker" && service.Platform != "linux-amd64")))
        {
            error = "verified_release_target_platform_invalid";
            return null;
        }

        Dictionary<string, string> digests = current.Services.ToDictionary(service => service.ServiceId, service => service.PlatformDigest, StringComparer.Ordinal);
        return new VerifiedHostUpdateCandidate(
            identity.ReleaseId,
            identity.SourceCommit,
            current.Sequence,
            current.ManifestDigest!,
            identity.Channel!,
            current.SignatureVerified,
            readiness.CompatibilityReady && HostUpdateValidation.IsSemanticVersion(current.MinimumUpdaterVersion),
            readiness.InstallationAvailable,
            readiness.SafetyPassed,
            true,
            readiness.IsNewer,
            new HostUpdatePlatformDigests(
                digests["api"],
                digests["frontend"],
                digests["slicer-host"],
                digests["printer-discovery"],
                digests["orcaslicer-worker"],
                digests["monolith"]),
            EvidenceFresh: true,
            VerifiedAt: verifiedAt);
    }

    private static bool IsCommit(string value) => value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);
}

/// <summary>
/// Explicitly refuses scheduler work while Dallas's executor contract cannot carry the scheduler
/// trust root, policy fingerprint, and canonical target binding without loss.
/// </summary>
public sealed class UnavailableHostUpdateSchedulerExecutor : IHostUpdateSchedulerExecutor
{
    public const string Reason = "executor_contract_missing_trust_root_policy_and_canonical_targets";

    public Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct) =>
        Task.FromResult(new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, Reason));

    public Task SignalSafeCheckpointCancellationAsync(string requestId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Reports registered-but-unavailable automatic updates without starting a hosted loop.</summary>
public sealed class UnavailableHostUpdateSchedulingStatusProvider(
    Farm.Infrastructure.Settings.ISettingsService settings) : IHostUpdateSchedulingStatusProvider
{
    public HostUpdateSchedulingStatusDto GetStatus()
    {
        HostUpdateAutomationSettings automation = settings.Get<HostUpdateAutomationSettings>();
        UpdateChannelSettings channel = settings.Get<UpdateChannelSettings>();
        return new HostUpdateSchedulingStatusDto
        {
            ConfiguredEnabled = automation.ConfiguredEnabled,
            EffectiveEnabled = false,
            SelectedChannel = channel.Channel,
            EffectiveChannel = null,
            PolicyRevision = automation.PolicyRevision,
            Backoff = new HostUpdateBackoffDto { State = HostUpdateBackoffState.Unknown, ConsecutiveFailures = 0, Reasons = ["scheduler_not_started"] },
            KillSwitch = new HostUpdateKillSwitchDto { Enabled = automation.KillSwitchEnabled, Reason = automation.KillSwitchEnabled ? "configured" : null },
            Executor = new HostUpdateExecutorDto { State = HostUpdateExecutorState.Unavailable, Reason = UnavailableHostUpdateSchedulerExecutor.Reason },
            Reasons = ["protected_replay_anchor_unavailable", "policy_mutation_facility_unavailable", UnavailableHostUpdateSchedulerExecutor.Reason],
        };
    }
}
