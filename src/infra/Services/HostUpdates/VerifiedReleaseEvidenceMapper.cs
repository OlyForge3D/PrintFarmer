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
    public static VerifiedReleaseEvidenceDto ToEvidenceDto(this SignedReleaseMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        List<ReleaseServiceRequirementDto> services = [];
        foreach ((string key, string digest) in metadata.ComponentPlatformDigests)
        {
            // Keys are always "{serviceId}/{platform}" — see
            // VerifiedGitHubReleaseMetadataProvider.GetCurrentAsync's construction of
            // ComponentPlatformDigests. Defensively skip any entry that doesn't parse rather
            // than throw, so a single malformed component can't take down the whole cache.
            int separator = key.IndexOf('/');
            if (separator <= 0 || separator == key.Length - 1)
            {
                continue;
            }

            services.Add(new ReleaseServiceRequirementDto
            {
                ServiceId = key[..separator],
                Platform = key[(separator + 1)..],
                PlatformDigest = digest,
            });
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
}
