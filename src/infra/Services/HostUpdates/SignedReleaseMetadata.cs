namespace Farm.Infrastructure.Services.HostUpdates;

public interface IHostUpdateMetadataProvider
{
    Task<SignedReleaseMetadata> GetCurrentAsync(string channel, CancellationToken ct);
}

public sealed record SignedReleaseMetadata(
    string Channel,
    long Sequence,
    bool SignatureVerified,
    CanonicalReleaseIdentity Identity,
    IReadOnlyDictionary<string, string> ComponentPlatformDigests,
    string MinimumUpdaterVersion,
    IReadOnlyDictionary<string, string>? ComponentIndexDigests = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ComponentPlatforms = null)
{
    public string CanonicalValue => string.Join(
        '|',
        Channel,
        Sequence,
        SignatureVerified,
        Identity?.CanonicalValue,
        MinimumUpdaterVersion,
        string.Join(',', ComponentPlatformDigests?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}") ?? []),
        string.Join(',', ComponentIndexDigests?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}") ?? []));
}

public sealed record CanonicalReleaseIdentity(
    string ReleaseId,
    string Version,
    string Channel,
    string SourceTag,
    string SourceBranch,
    string SourceCommit,
    string AuthorizedBranchHead,
    string BuildMetadata,
    string OciReleaseLabel,
    string OciVersionLabel,
    string ManifestDigest)
{
    public string CanonicalValue => string.Join(
        '|',
        ReleaseId,
        Version,
        Channel,
        SourceTag,
        SourceBranch,
        SourceCommit,
        AuthorizedBranchHead,
        BuildMetadata,
        OciReleaseLabel,
        OciVersionLabel,
        ManifestDigest);
}
