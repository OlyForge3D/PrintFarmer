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

        IReadOnlyList<VerifiedReleaseExecutionTargetDto> executionTargets = current.ExecutionTargets.Count == HostUpdateExecutionRequest.RequiredTargetCount
            ? current.ExecutionTargets
            : current.Services.Select(service => new VerifiedReleaseExecutionTargetDto
            {
                ServiceId = service.ServiceId switch { "discovery" => "printer-discovery", "slicer-worker" => "orcaslicer-worker", _ => service.ServiceId },
                Platform = service.Platform,
                PlatformDigest = service.PlatformDigest,
            }).ToArray();
        if (executionTargets.Count != HostUpdateExecutionRequest.RequiredTargetCount
            || executionTargets.Any(target => !HostUpdateExecutionRequest.RequiredServiceIds.Contains(target.ServiceId))
            || executionTargets.Select(target => target.ServiceId).Distinct(StringComparer.Ordinal).Count() != HostUpdateExecutionRequest.RequiredTargetCount)
        {
            error = "verified_release_target_set_invalid";
            return null;
        }

        if (executionTargets.Any(target => !string.Equals(target.Platform, hostPlatform, StringComparison.Ordinal)
            || !HostUpdateValidation.IsDigest(target.PlatformDigest)))
        {
            error = hostPlatform == "linux-arm64" ? "verified_release_target_platform_unavailable" : "verified_release_target_platform_invalid";
            return null;
        }

        Dictionary<string, string> digests = executionTargets.ToDictionary(target => target.ServiceId, target => target.PlatformDigest, StringComparer.Ordinal);
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

public static class HostUpdateSchedulingAvailability
{
    public const string ExecutorNotProvisionedReason = "automatic_scheduler_executor_not_provisioned";
    public const string ProtectedReplayAnchorReason = "protected_replay_anchor_unavailable";
    public const string PolicyMutationReason = "policy_mutation_facility_unavailable";
}

/// <summary>Reports registered-but-unavailable automatic updates without starting a hosted loop.</summary>
public sealed class UnavailableHostUpdateSchedulingStatusProvider(
    Farm.Infrastructure.Settings.ISettingsService settings,
    IHostUpdateAutomationPolicyRepository? policyRepository = null) : IHostUpdateSchedulingStatusProvider
{
    public HostUpdateSchedulingStatusDto GetStatus()
    {
        _ = settings;
        HostUpdatePolicyReadResult policyResult = policyRepository?.Read() ?? new(true, new HostUpdateAutomationPolicy(), null);
        HostUpdateAutomationPolicy policy = policyResult.Policy;
        string selectedChannel = policyResult.Available ? policy.Channel : UpdateChannelSettings.StableChannel;
        IReadOnlyList<string> reasons = policyResult.Available
            ? [HostUpdateSchedulingAvailability.ProtectedReplayAnchorReason, HostUpdateSchedulingAvailability.PolicyMutationReason, HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason]
            : ["host_update_policy_unavailable", HostUpdateSchedulingAvailability.ProtectedReplayAnchorReason, HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason];
        return new HostUpdateSchedulingStatusDto
        {
            ConfiguredEnabled = policy.Enabled,
            EffectiveEnabled = false,
            SelectedChannel = selectedChannel,
            EffectiveChannel = null,
            PolicyRevision = policy.Revision,
            Backoff = new HostUpdateBackoffDto { State = HostUpdateBackoffState.Unknown, ConsecutiveFailures = 0, Reasons = ["scheduler_not_started"] },
            KillSwitch = new HostUpdateKillSwitchDto { Enabled = policy.KillSwitch, Reason = policy.KillSwitch ? "configured" : null },
            Executor = new HostUpdateExecutorDto { State = HostUpdateExecutorState.Unavailable, Reason = HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason },
            Reasons = reasons,
        };
    }
}
