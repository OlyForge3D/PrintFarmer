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

        List<ReleaseServiceRequirementDto> services = [];
        foreach ((string key, string digest) in metadata.ComponentPlatformDigests)
        {
            int separator = key.IndexOf('/');
            if (separator <= 0
                || separator == key.Length - 1
                || key.IndexOf('/', separator + 1) >= 0)
            {
                throw new InvalidDataException(
                    $"Verified release component platform key '{key}' is invalid.");
            }

            string platform = key[(separator + 1)..];
            if (platform != hostPlatform)
            {
                continue;
            }

            services.Add(new ReleaseServiceRequirementDto
            {
                ServiceId = key[..separator],
                Platform = platform,
                PlatformDigest = digest,
            });
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
            BaseVersion = metadata.Identity.Version,
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
            Identity = identity,
            ManifestDigest = metadata.Identity.ManifestDigest,
            Services = services,
        };
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
