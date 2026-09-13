using System.Text.RegularExpressions;
using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.SystemStatus;

/// <summary>Evaluates already collected evidence, without deriving releases or checking moving references.</summary>
public static partial class ServiceInventoryEvaluator
{
    /// <summary>Separates freshness, release compatibility, channel selection and eligibility.</summary>
    public static ServiceInventoryDto Evaluate(
        IEnumerable<ServiceReplicaObservationDto> observations, string? selectedChannel, DateTimeOffset now)
    {
        string selection = selectedChannel == "insider" ? "insider" : "stable";
        ServiceReplicaObservationDto[] replicas = observations.Select(row => Normalize(row, selection, now)).ToArray();
        ServiceReplicaObservationDto[] installed = replicas.Where(row => row.ObservationState != InventoryObservationState.NotInstalled).ToArray();
        string[] channels = installed.Select(row => row.ObservedChannel).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        InventoryCompatibilityState compatibility = GetCompatibility(installed);
        string[] reasons = [compatibility switch
        {
            InventoryCompatibilityState.MixedChannel => "MixedApplicationChannelsUnsafe",
            InventoryCompatibilityState.MixedRelease => "MixedApplicationReleases",
            InventoryCompatibilityState.Incompatible => "ConflictingReplicaDigests",
            InventoryCompatibilityState.Compatible => "SameVerifiedReleaseSet",
            _ => "IncompleteReleaseOrPlatformEvidence",
        }];
        InventoryChannelState channelState = channels.Length > 1 ? InventoryChannelState.Mixed
            : installed.Any(row => row.ChannelState == InventoryChannelState.Mismatch) ? InventoryChannelState.Mismatch
            : installed.Any(row => row.ObservationState == InventoryObservationState.Stale) ? InventoryChannelState.Stale
            : installed.Length == 0 || installed.Any(row => row.ChannelState == InventoryChannelState.Unknown) ? InventoryChannelState.Unknown
            : InventoryChannelState.Observed;
        bool blocked = compatibility is InventoryCompatibilityState.MixedChannel or InventoryCompatibilityState.MixedRelease or InventoryCompatibilityState.Incompatible
            || channelState == InventoryChannelState.Mismatch;
        return new ServiceInventoryDto
        {
            SelectedChannel = selection,
            SelectionSource = selectedChannel is "stable" or "insider" ? "HostConfiguration" : "Default",
            CollectedAt = now,
            ObservedChannel = channels.Length == 1 && installed.All(row => row.ObservedChannel is not null) ? channels[0] : null,
            ChannelState = channelState,
            CompatibilityState = compatibility,
            CompatibilityReasons = reasons,
            Eligibility = blocked ? InventoryEligibility.Blocked : InventoryEligibility.NotManaged,
            EligibilityReasons = blocked ? [.. reasons, "ReadOnlyInventory"] : ["ManagedEligibilityNotEstablished", "ReadOnlyInventory"],
            Services = replicas.Select(row => row with
            {
                CompatibilityState = row.ObservationState == InventoryObservationState.NotInstalled ? InventoryCompatibilityState.Unknown : compatibility,
                CompatibilityReasons = row.ObservationState == InventoryObservationState.NotInstalled ? ["OptionalNotInstalled"] : reasons,
            }).ToArray(),
        };
    }

    // Source adapters retain original timestamps; reading an old snapshot cannot make it fresh.
    private static ServiceReplicaObservationDto Normalize(ServiceReplicaObservationDto row, string selection, DateTimeOffset now)
    {
        if (row.ObservationState == InventoryObservationState.NotInstalled && !row.Required)
        {
            return row with
            {
                Identity = null, ObservedChannel = null, ChannelState = InventoryChannelState.Unknown,
                PlatformDigest = null, IndexDigest = null, ManifestDigest = null,
                VerificationSource = null, VerifiedAt = null,
            };
        }

        // A source cannot make required absence healthy by labelling it optional absence.
        if (row.ObservationState == InventoryObservationState.NotInstalled)
        {
            row = row with { ObservationState = InventoryObservationState.Unavailable, ReasonCode = "RequiredServiceAbsent" };
        }

        bool verified = HasBoundAuthorization(row);
        InventoryObservationState state = row.ObservationState;
        if (row.ObservedAt > now || row.LastSuccessAt > now || row.VerifiedAt > now)
        {
            state = InventoryObservationState.Unknown;
            verified = false;
        }
        else if (state == InventoryObservationState.Observed && row.ObservedAt is null)
        {
            state = InventoryObservationState.Unknown;
        }
        else if (state == InventoryObservationState.Observed && now - row.ObservedAt > TimeSpan.FromSeconds(90))
        {
            state = InventoryObservationState.Stale;
        }

        string? channel = verified ? row.Identity!.Channel : null;
        return row with
        {
            Identity = verified ? row.Identity : null,
            VerificationSource = verified ? row.VerificationSource : null,
            VerifiedAt = verified ? row.VerifiedAt : null,
            PlatformDigest = row.Source == "SelfReport" ? null : NormalizeDigest(row.PlatformDigest),
            IndexDigest = row.Source == "SelfReport" ? null : NormalizeDigest(row.IndexDigest),
            ManifestDigest = row.Source == "SelfReport" ? null : NormalizeDigest(row.ManifestDigest),
            ObservedChannel = channel,
            ObservationState = state,
            ChannelState = channel is null ? InventoryChannelState.Unknown
                : channel != selection ? InventoryChannelState.Mismatch
                : state == InventoryObservationState.Stale ? InventoryChannelState.Stale
                : state == InventoryObservationState.Observed ? InventoryChannelState.Observed : InventoryChannelState.Unknown,
        };
    }

    // This is a consistency check on a trusted adapter's result, NOT signature verification.
    // #2668 owns authorization and derivation; #2660 owns signing/verification implementation.
    // No self-report adapter in this increment supplies verified canonical evidence.
    private static bool HasBoundAuthorization(ServiceReplicaObservationDto row)
    {
        CanonicalReleaseIdentityDto? identity = row.Identity;
        if (identity is null || row.Source == "SelfReport" || row.VerifiedAt is null
            || string.IsNullOrWhiteSpace(row.VerificationSource)
            || identity.SourceCommit is null || !FullCommit().IsMatch(identity.SourceCommit)
            || identity.SourceCommit != identity.AuthorizedBranchHead || row.SourceCommit != identity.SourceCommit
            || string.IsNullOrWhiteSpace(identity.BuildId) || string.IsNullOrWhiteSpace(identity.BuildAttempt)
            || string.IsNullOrWhiteSpace(identity.WorkflowIdentity) || string.IsNullOrWhiteSpace(identity.AllocationIdentity))
        {
            return false;
        }

        // Validate the shared record's binding; never synthesize a missing field from a tag.
        return identity.SourceTag == $"v{identity.CanonicalVersion}"
            && identity.ReleaseId == $"{identity.Channel}:{identity.CanonicalVersion}"
            && identity.BaseVersion is not null && StableVersion().IsMatch(identity.BaseVersion)
            && ((identity.Channel == "stable" && identity.SourceBranch == "main" && identity.CanonicalVersion == identity.BaseVersion)
                || (identity.Channel == "insider" && identity.SourceBranch == "development"
                    && identity.CanonicalVersion is not null && InsiderVersion().IsMatch(identity.CanonicalVersion)
                    && identity.CanonicalVersion.StartsWith(identity.BaseVersion + "-insider.", StringComparison.Ordinal)));
    }

    private static InventoryCompatibilityState GetCompatibility(ServiceReplicaObservationDto[] rows)
    {
        if (rows.Select(row => row.ObservedChannel).OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            return InventoryCompatibilityState.MixedChannel;
        }

        // Different components legitimately have different platform digests. Only like-for-like replicas conflict.
        if (rows.Where(row => row.PlatformDigest is not null).GroupBy(row => (row.ServiceId, row.Platform, row.Identity?.CanonicalVersion))
            .Any(group => group.Select(row => row.PlatformDigest).Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            return InventoryCompatibilityState.Incompatible;
        }

        if (rows.Select(row => row.Identity?.ReleaseId).OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any()
            || rows.Select(row => row.ApplicationVersion).OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any()
            || rows.Select(row => row.SourceCommit).OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any()
            || rows.Select(row => row.ManifestDigest).OfType<string>().Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            return InventoryCompatibilityState.MixedRelease;
        }

        return rows.Length > 0 && rows.All(row => row.Identity is not null && !string.IsNullOrWhiteSpace(row.Platform) && row.PlatformDigest is not null && row.ManifestDigest is not null)
            ? InventoryCompatibilityState.Compatible : InventoryCompatibilityState.Unknown;
    }

    private static string? NormalizeDigest(string? digest) =>
        digest is not null && Sha256Digest().IsMatch(digest) ? digest : null;

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Digest();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullCommit();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersion();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)-insider\\.[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex InsiderVersion();
}
