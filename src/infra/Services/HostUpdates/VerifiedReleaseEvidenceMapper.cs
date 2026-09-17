using System.Runtime.InteropServices;
using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Maps <see cref="SignedReleaseMetadata"/> (the host-update-domain shape produced by
/// <see cref="IHostUpdateMetadataProvider"/>/<see cref="VerifiedGitHubReleaseMetadataProvider"/>)
/// to <see cref="VerifiedReleaseEvidenceDto"/> (the inventory/readiness-domain shape consumed by
/// <c>ReleaseReadinessEvaluator.Evaluate</c>), for issue #2757's production discovery wiring.
/// <para>
/// These are two distinct, independently owned identity shapes:
/// <see cref="CanonicalReleaseIdentity"/> (host-update domain, this project's
/// <c>HostUpdates</c> namespace) versus <see cref="CanonicalReleaseIdentityDto"/> (inventory
/// domain, owned by issue #2668 and consumed by <c>ServiceInventoryEvaluator</c>). Only the
/// fields <c>ReleaseReadinessEvaluator</c> actually reads are load-bearing here: it inspects
/// <see cref="VerifiedReleaseEvidenceDto.SignatureVerified"/>,
/// <see cref="VerifiedReleaseEvidenceDto.IsComplete"/>,
/// <see cref="VerifiedReleaseEvidenceDto.Identity"/> (non-null and its <c>Channel</c>),
/// <see cref="VerifiedReleaseEvidenceDto.ManifestDigest"/>, and
/// <see cref="VerifiedReleaseEvidenceDto.Services"/>. Fields with no inventory-domain analogue
/// (<c>BuildAttempt</c>, <c>WorkflowIdentity</c>, <c>AllocationIdentity</c>,
/// <c>PromotionOrigin</c>) are left <c>null</c> — there is no bound-authorization claim to copy
/// them from, and inventing values would misrepresent evidence strength.
/// </para>
/// </summary>
public static class VerifiedReleaseEvidenceMapper
{
    private static readonly Dictionary<string, string> InventoryServiceIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api"] = "api",
            ["frontend"] = "frontend",
            ["slicer-host"] = "slicer-host",
            ["printer-discovery"] = "discovery",
            ["orcaslicer-worker"] = "slicer-worker",
        };

    /// <summary>
    /// Maps a fully verified <see cref="SignedReleaseMetadata"/> — which
    /// <see cref="VerifiedGitHubReleaseMetadataProvider.GetCurrentAsync"/> only ever returns
    /// after manifest schema/content validation and Cosign signature verification both
    /// succeeded — to the DTO shape <c>ReleaseReadinessEvaluator</c> consumes.
    /// </summary>
    public static VerifiedReleaseEvidenceDto ToEvidenceDto(this SignedReleaseMetadata metadata) =>
        ToEvidenceDto(metadata, GetHostPlatform());

    /// <summary>Maps verified metadata for one explicit host platform.</summary>
    internal static VerifiedReleaseEvidenceDto ToEvidenceDto(
        this SignedReleaseMetadata metadata,
        string hostPlatform)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPlatform);
        if (!SignedUpdateManifestValidator.IsPlatform(hostPlatform))
        {
            throw new InvalidDataException($"Host platform '{hostPlatform}' is invalid.");
        }

        List<ReleaseServiceRequirementDto> services = [];
        if (metadata.ComponentPlatforms is null
            || metadata.ComponentIndexDigests is null
            || metadata.ComponentPlatformDigests is null)
        {
            throw new InvalidDataException("Verified release service platform or index evidence is missing.");
        }

        foreach ((string manifestServiceId, IReadOnlyList<string> platforms) in metadata.ComponentPlatforms)
        {
            if (manifestServiceId == "monolith")
            {
                continue;
            }

            if (!InventoryServiceIds.TryGetValue(manifestServiceId, out string? inventoryServiceId)
                || platforms is null
                || platforms.Count == 0
                || platforms.Distinct(StringComparer.Ordinal).Count() != platforms.Count
                || platforms.Any(platform => !SignedUpdateManifestValidator.IsPlatform(platform)))
            {
                throw new InvalidDataException(
                    $"Verified release service '{manifestServiceId}' has invalid platform evidence.");
            }

            if (!platforms.Contains(hostPlatform, StringComparer.Ordinal))
            {
                continue;
            }

            string key = SignedUpdateManifestValidator.PlatformKey(manifestServiceId, hostPlatform);
            if (!metadata.ComponentPlatformDigests.TryGetValue(key, out string? digest)
                || !metadata.ComponentIndexDigests.TryGetValue(manifestServiceId, out string? indexDigest)
                || !HostUpdateValidation.IsDigest(digest)
                || !HostUpdateValidation.IsDigest(indexDigest))
            {
                throw new InvalidDataException(
                    $"Verified release service '{manifestServiceId}' is missing immutable digest evidence for '{hostPlatform}'.");
            }

            services.Add(new ReleaseServiceRequirementDto
            {
                ServiceId = inventoryServiceId,
                Platform = hostPlatform,
                PlatformDigest = digest,
                IndexDigest = indexDigest,
            });
        }

        if (services.Count == 0)
        {
            throw new InvalidDataException(
                $"Verified release has no service targets for host platform '{hostPlatform}'.");
        }

        if (services.GroupBy(service => service.ServiceId, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new InvalidDataException(
                $"Verified release contains duplicate service targets for host platform '{hostPlatform}'.");
        }

        CanonicalReleaseIdentityDto identity = new()
        {
            CanonicalVersion = metadata.Identity.Version,
            BaseVersion = GetBaseVersion(metadata.Identity.Version, metadata.Identity.Channel),
            Channel = metadata.Identity.Channel,
            ReleaseId = metadata.Identity.ReleaseId,
            SourceTag = metadata.Identity.SourceTag,
            SourceBranch = metadata.Identity.SourceBranch,
            SourceCommit = metadata.Identity.SourceCommit,
            AuthorizedBranchHead = metadata.Identity.AuthorizedBranchHead,
            BuildId = metadata.Identity.BuildMetadata,
        };

        return new VerifiedReleaseEvidenceDto
        {
            // VerifiedGitHubReleaseMetadataProvider.GetCurrentAsync throws InvalidDataException
            // rather than returning a SignedReleaseMetadata whose manifest failed validation or
            // whose Cosign signature failed verification — by the time this method is called,
            // SignatureVerified/IsComplete are both unconditionally true.
            SignatureVerified = metadata.SignatureVerified,
            IsComplete = true,
            Sequence = metadata.Sequence,
            MinimumUpdaterVersion = metadata.MinimumUpdaterVersion,
            Identity = identity,
            ManifestDigest = metadata.Identity.ManifestDigest,
            Services = services,
        };
    }

    private static string GetBaseVersion(string version, string channel)
    {
        if (channel == "insider")
        {
            int delimiter = version.IndexOf("-insider.", StringComparison.Ordinal);
            if (delimiter > 0)
            {
                return version[..delimiter];
            }
        }

        return version;
    }

    private static string GetHostPlatform()
    {
        string operatingSystem = OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : throw new PlatformNotSupportedException("The current operating system is not supported for signed release discovery.");
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException(
                $"Architecture '{RuntimeInformation.ProcessArchitecture}' is not supported for signed release discovery."),
        };
        return $"{operatingSystem}-{architecture}";
    }
}
