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
            VerifiedAt: verifiedAt,
            HostPlatform: hostPlatform);
    }

    private static bool IsCommit(string value) => value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);
}

public static class HostUpdateSchedulingAvailability
{
    public const string ExecutorNotProvisionedReason = "automatic_scheduler_executor_not_provisioned";
    public const string ExecutorAvailabilityUnknownReason = "automatic_scheduler_executor_availability_unknown";
    public const string ExecutorReasonMissingReason = "automatic_scheduler_executor_reason_missing";
    public const string ProtectedReplayAnchorReason = "protected_replay_anchor_unavailable";
    public const string PolicyMutationReason = "policy_mutation_facility_unavailable";
    public const string AdmissionFenceReason = "host_update_recovery_unavailable";
}

/// <summary>Reports registered-but-unavailable automatic updates without starting a hosted loop.</summary>
public sealed class UnavailableHostUpdateSchedulingStatusProvider(
    Farm.Infrastructure.Settings.ISettingsService settings,
    HostUpdateSchedulerStatusHolder? schedulerStatus = null,
    IHostUpdateAutomationPolicyRepository? policyRepository = null,
    IHostUpdateReplayAnchor? replayAnchor = null,
    IHostUpdateReplayStore? replayStore = null,
    IHostUpdateExecutor? executor = null,
    IHostUpdateAdmissionFence? admissionFence = null) : IHostUpdateSchedulingStatusProvider
{
    public HostUpdateSchedulingStatusDto GetStatus()
    {
        _ = settings;
        if (schedulerStatus is not null)
        {
            HostUpdateSchedulerStatus current = schedulerStatus.Current;
            HostUpdatePolicyReadResult holderPolicyResult = policyRepository?.Read() ?? new(true, new HostUpdateAutomationPolicy(), null);
            HostUpdateAutomationPolicy holderPolicy = holderPolicyResult.Available
                ? holderPolicyResult.Policy
                : new HostUpdateAutomationPolicy();
            List<string> holderReasons = [];
            if (!holderPolicyResult.Available)
            {
                AddReason(holderReasons, holderPolicyResult.Error ?? "host_update_policy_unavailable");
            }

            AddUnavailable(holderReasons, replayAnchor, HostUpdateSchedulingAvailability.ProtectedReplayAnchorReason);
            AddUnavailable(holderReasons, replayStore, "host_update_replay_store_unavailable");
            HostUpdateAdmissionFenceStatus? holderAdmission = admissionFence?.GetStatus();
            if (holderAdmission?.BlocksAdmission == true)
            {
                AddReason(holderReasons, string.IsNullOrWhiteSpace(holderAdmission.Reason) ? HostUpdateSchedulingAvailability.AdmissionFenceReason : holderAdmission.Reason);
            }

            string? holderExecutorReason = executor switch
            {
                IHostUpdateAvailability { IsAvailable: false } holderExecutorAvailability =>
                    NormalizeUnavailableReason(holderExecutorAvailability.UnavailableReason),
                IHostUpdateAvailability { IsAvailable: true } => null,
                null => HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason,
                _ => HostUpdateSchedulingAvailability.ExecutorAvailabilityUnknownReason
            };
            bool hasAttempt = current.LastAttemptAt is not null;
            bool dependencyBlocked = holderReasons.Count > 0;
            if (hasAttempt && (!dependencyBlocked || current.Reason is not HostUpdateSchedulerReason.Disabled))
            {
                AddReason(holderReasons, current.Reason.ToString());
            }

            if (holderExecutorReason is not null)
            {
                AddReason(holderReasons, holderExecutorReason);
            }

            if (holderReasons.Count == 0)
            {
                AddReason(holderReasons, current.Reason.ToString());
            }

            return new HostUpdateSchedulingStatusDto
            {
                ConfiguredEnabled = current.Enabled,
                EffectiveEnabled = current.EffectiveEnabled,
                SelectedChannel = current.Channel,
                EffectiveChannel = current.EffectiveEnabled ? current.Channel : null,
                PolicyRevision = current.PolicyRevision,
                LastAttemptAt = current.LastAttemptAt,
                NextAttemptAt = current.NextPollAt,
                Backoff = new HostUpdateBackoffDto
                {
                    State = current.NextPollAt is null
                        ? hasAttempt && !dependencyBlocked ? HostUpdateBackoffState.Due : HostUpdateBackoffState.Unknown
                        : current.NextPollAt > DateTimeOffset.UtcNow
                            ? HostUpdateBackoffState.Waiting
                            : HostUpdateBackoffState.Due,
                    ConsecutiveFailures = current.ConsecutiveFailures,
                    Until = current.NextPollAt,
                    Reasons = [current.Reason.ToString()],
                },
                KillSwitch = new HostUpdateKillSwitchDto
                {
                    Enabled = current.KillSwitch || holderPolicy.KillSwitch,
                    Reason = (current.KillSwitch || holderPolicy.KillSwitch) ? "configured" : null,
                },
                Executor = new HostUpdateExecutorDto
                {
                    State = holderExecutorReason is null
                        ? HostUpdateExecutorState.Available
                        : HostUpdateExecutorState.Unavailable,
                    Reason = holderExecutorReason,
                },
                Reasons = holderReasons.ToArray(),
            };
        }

        HostUpdatePolicyReadResult policyResult = policyRepository?.Read() ?? new(true, new HostUpdateAutomationPolicy(), null);
        HostUpdateAutomationPolicy policy = policyResult.Available ? policyResult.Policy : new HostUpdateAutomationPolicy();
        string selectedChannel = policyResult.Available ? policy.Channel : UpdateChannelSettings.StableChannel;
        List<string> reasons = [];
        if (!policyResult.Available)
        {
            AddReason(reasons, policyResult.Error ?? "host_update_policy_unavailable");
        }

        AddUnavailable(reasons, replayAnchor, HostUpdateSchedulingAvailability.ProtectedReplayAnchorReason);
        AddUnavailable(reasons, replayStore, "host_update_replay_store_unavailable");
        HostUpdateAdmissionFenceStatus? admission = admissionFence?.GetStatus();
        if (admission?.BlocksAdmission == true)
        {
            AddReason(reasons, string.IsNullOrWhiteSpace(admission.Reason) ? HostUpdateSchedulingAvailability.AdmissionFenceReason : admission.Reason);
        }

        string executorReason = executor switch
        {
            IHostUpdateAvailability { IsAvailable: false } executorAvailability =>
                NormalizeUnavailableReason(executorAvailability.UnavailableReason),
            IHostUpdateAvailability { IsAvailable: true } =>
                HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason,
            null => HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason,
            _ => HostUpdateSchedulingAvailability.ExecutorAvailabilityUnknownReason
        };
        AddUnavailable(reasons, executor, executorReason);
        if (executor is not null and not IHostUpdateAvailability)
        {
            AddReason(reasons, executorReason);
        }

        if (reasons.Count == 0)
        {
            AddReason(reasons, "scheduler_not_started");
        }

        return new HostUpdateSchedulingStatusDto
        {
            ConfiguredEnabled = policy.Enabled,
            EffectiveEnabled = false,
            SelectedChannel = selectedChannel,
            EffectiveChannel = null,
            PolicyRevision = policy.Revision,
            Backoff = new HostUpdateBackoffDto { State = HostUpdateBackoffState.Unknown, ConsecutiveFailures = 0, Reasons = ["scheduler_not_started"] },
            KillSwitch = new HostUpdateKillSwitchDto { Enabled = policy.KillSwitch, Reason = policy.KillSwitch ? "configured" : null },
            Executor = new HostUpdateExecutorDto { State = HostUpdateExecutorState.Unavailable, Reason = executorReason },
            Reasons = reasons.ToArray(),
        };
    }

    private static void AddUnavailable(List<string> reasons, object? service, string fallback)
    {
        if (service is IHostUpdateAvailability { IsAvailable: false } availability)
        {
            AddReason(reasons, string.IsNullOrWhiteSpace(availability.UnavailableReason) ? fallback : availability.UnavailableReason);
        }
        else if (service is null)
        {
            AddReason(reasons, fallback);
        }
    }

    private static string NormalizeUnavailableReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? HostUpdateSchedulingAvailability.ExecutorReasonMissingReason
            : reason;

    private static void AddReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal))
        {
            reasons.Add(reason);
        }
    }
}
