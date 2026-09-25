namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// The release trust root this build pins (issue #3047): the Sigstore OIDC issuer and the
/// per-channel release-workflow certificate identities Cosign verification requires. The
/// fingerprint is journaled at authorization so recovery can detect a changed trust root.
/// </summary>
public static class HostUpdateTrustRoot
{
    /// <summary>Trust-root id carried by verified candidates and journaled request bindings.</summary>
    public const string DefaultTrustRoot = "default";

    public const string CosignIssuer = "https://token.actions.githubusercontent.com";

    public const string StableCertificateIdentity =
        "https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main";

    public const string InsiderCertificateIdentity =
        "https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development";

    /// <summary>Fingerprint of every pinned trust-root input.</summary>
    public static string Fingerprint { get; } = "sha256:" + HostUpdateBaselineHashes.Hash(new
    {
        TrustRoots = new[] { DefaultTrustRoot },
        Issuer = CosignIssuer,
        Stable = StableCertificateIdentity,
        Insider = InsiderCertificateIdentity,
    });

    /// <summary>The certificate identity a channel's release manifest must be signed by.</summary>
    public static string CertificateIdentity(string channel) =>
        channel == "stable" ? StableCertificateIdentity : InsiderCertificateIdentity;

    public static bool IsPinned(string? trustRoot) => string.Equals(trustRoot, DefaultTrustRoot, StringComparison.Ordinal);
}
