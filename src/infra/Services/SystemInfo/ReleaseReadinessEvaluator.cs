using System.Text.RegularExpressions;
using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>Evaluates a signed target against collected evidence without contacting a host or executing an update.</summary>
public static partial class ReleaseReadinessEvaluator
{
    /// <summary>Returns a fail-closed readiness decision for a target release and inventory snapshot.</summary>
    public static ReleaseReadinessDto Evaluate(ServiceInventoryDto inventory, VerifiedReleaseEvidenceDto? release, DateTimeOffset now)
    {
        List<string> hops = ["InventoryRead"];
        if (release is null)
        {
            return Result(InventoryEligibility.NotManaged, ["NoSignedReleaseSelected"], hops);
        }

        hops.Add("SignedReleaseEvidence");
        if (!release.SignatureVerified || !release.IsComplete || release.Identity is null || !Digest().IsMatch(release.ManifestDigest ?? string.Empty))
        {
            return Result(InventoryEligibility.Blocked, ["ReleaseEvidenceIncompleteOrUnverified"], hops);
        }

        if (inventory.SnapshotOrigin == InventorySnapshotOrigin.Imported)
        {
            return Result(InventoryEligibility.Unknown, ["ImportedSnapshotIsNotLiveObservation"], hops);
        }

        hops.Add("FreshHostEvidence");
        ServiceReplicaObservationDto[] required = inventory.Services.Where(service => service.Required).ToArray();
        if (required.Length == 0 || required.Any(service => !HasFreshIndependentEvidence(service, now)))
        {
            return Result(InventoryEligibility.Unknown, ["RequiredHostEvidenceMissingOrStale"], hops);
        }

        if (inventory.CompatibilityState != InventoryCompatibilityState.Compatible || inventory.ChannelState != InventoryChannelState.Observed)
        {
            return Result(InventoryEligibility.Blocked, ["InstalledTopologyIncompatible"], hops);
        }

        hops.Add("TargetCompatibility");
        if (release.Identity.Channel != inventory.SelectedChannel)
        {
            return Result(InventoryEligibility.Blocked, ["TargetChannelDoesNotMatchSelection"], hops);
        }

        if (release.Services.GroupBy(service => service.ServiceId, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
        {
            return Result(InventoryEligibility.Blocked, ["DuplicateTargetServiceEvidence"], hops);
        }

        foreach (ServiceReplicaObservationDto service in required)
        {
            if (!release.Services.Any(target => target.ServiceId == service.ServiceId))
            {
                return Result(InventoryEligibility.Blocked, [$"MissingTargetService:{service.ServiceId}"], hops);
            }
        }

        foreach (ReleaseServiceRequirementDto target in release.Services)
        {
            ServiceReplicaObservationDto[] matchingServices = inventory.Services
                .Where(service => service.ServiceId == target.ServiceId)
                .ToArray();
            if (matchingServices.Length == 0)
            {
                return Result(InventoryEligibility.Blocked, [$"RequiredServiceInventoryMissing:{target.ServiceId}"], hops);
            }

            if (matchingServices.Any(service => !HasFreshIndependentEvidence(service, now)))
            {
                return Result(InventoryEligibility.Unknown, [$"RequiredServiceEvidenceMissingOrStale:{target.ServiceId}"], hops);
            }

            foreach (ServiceReplicaObservationDto service in matchingServices)
            {
                if (!string.Equals(service.Platform, target.Platform, StringComparison.Ordinal)
                    || !Digest().IsMatch(service.PlatformDigest ?? string.Empty)
                    || !Digest().IsMatch(target.PlatformDigest))
                {
                    return Result(InventoryEligibility.Blocked, [$"PlatformMismatchOrInvalidDigestEvidence:{service.ServiceId}"], hops);
                }

                if (target.RequiredMigrationHead is not null && !string.Equals(service.MigrationHead, target.RequiredMigrationHead, StringComparison.Ordinal))
                {
                    return Result(InventoryEligibility.Blocked, [$"MigrationHeadMismatch:{service.ServiceId}"], hops);
                }

                if (target.RequiredEngineVersion is not null && !string.Equals(service.EngineVersion, target.RequiredEngineVersion, StringComparison.Ordinal))
                {
                    return Result(InventoryEligibility.Blocked, [$"WorkerCompatibilityMismatch:{service.ServiceId}"], hops);
                }
            }
        }

        return Result(InventoryEligibility.Eligible, [], hops);
    }

    private static ReleaseReadinessDto Result(InventoryEligibility state, IReadOnlyList<string> reasons, IReadOnlyList<string> hops) =>
        new() { State = state, Reasons = reasons, Hops = Array.AsReadOnly(hops.ToArray()) };

    private static bool HasFreshIndependentEvidence(ServiceReplicaObservationDto service, DateTimeOffset now) =>
        service.ObservationState == InventoryObservationState.Observed
        && service.Source != "SelfReport"
        && service.Identity is not null
        && !string.IsNullOrWhiteSpace(service.VerificationSource)
        && service.ObservedAt is not null
        && service.LastSuccessAt is not null
        && service.VerifiedAt is not null
        && service.ObservedAt <= now
        && service.LastSuccessAt <= now
        && service.VerifiedAt <= now
        && now - service.ObservedAt <= TimeSpan.FromSeconds(90)
        && now - service.LastSuccessAt <= TimeSpan.FromSeconds(90)
        && now - service.VerifiedAt <= TimeSpan.FromSeconds(90);

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
}
