using Farm.Slicer.Worker.Core;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Covers the identity a slicer worker is allowed to report at registration. A stub image must never
/// be able to advertise a pinned binary, because the API turns that claim into a calibration
/// capability.
/// </summary>
public sealed class SlicerBinaryAttestationTests
{
    private const string AttestedDigest = "3A1F4C0B9E2D8A7C6B5E4D3C2B1A09F8E7D6C5B4A39281706F5E4D3C2B1A0918";

    [Fact(DisplayName = "A verified installed binary reports the attested digest")]
    public void Resolve_WithAttestedRealBinary_ReportsPinnedIdentity()
    {
        SlicerBinaryIdentity identity = SlicerBinaryAttestation.Resolve(
            AttestedDigest,
            AttestedDigest,
            realBinaryInstalled: true);

        _ = identity.RealBinary.Should().BeTrue();
        _ = identity.BinarySha256.Should().Be(AttestedDigest);
    }

    [Fact(DisplayName = "A stub image cannot report a pinned identity even when the build declared one")]
    public void Resolve_WithStubBinaryAndDeclaredDigest_ReportsUnverified()
    {
        SlicerBinaryIdentity identity = SlicerBinaryAttestation.Resolve(
            attestedSha256: string.Empty,
            declaredSha256: AttestedDigest,
            realBinaryInstalled: false);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }

    [Fact(DisplayName = "A stub image cannot report a pinned identity even when an attestation exists")]
    public void Resolve_WithStubBinaryAndAttestedDigest_ReportsUnverified()
    {
        SlicerBinaryIdentity identity = SlicerBinaryAttestation.Resolve(
            AttestedDigest,
            AttestedDigest,
            realBinaryInstalled: false);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }

    [Fact(DisplayName = "A declared digest alone never establishes an identity")]
    public void Resolve_WithDeclaredDigestOnly_ReportsUnverified()
    {
        SlicerBinaryIdentity identity = SlicerBinaryAttestation.Resolve(
            attestedSha256: string.Empty,
            declaredSha256: AttestedDigest,
            realBinaryInstalled: true);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }

    [Fact(DisplayName = "A declared digest that disagrees with the attestation is refused")]
    public void Resolve_WithDisagreeingDeclaredDigest_ReportsUnverified()
    {
        SlicerBinaryIdentity identity = SlicerBinaryAttestation.Resolve(
            AttestedDigest,
            declaredSha256: new string('A', 64),
            realBinaryInstalled: true);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }

    [Theory(DisplayName = "A malformed attestation is treated as no attestation")]
    [InlineData("not-a-digest")]
    [InlineData("3A1F")]
    [InlineData("   ")]
    public void Resolve_WithMalformedAttestation_ReportsUnverified(string attested)
    {
        SlicerBinaryIdentity identity = SlicerBinaryAttestation.Resolve(
            attested,
            declaredSha256: null,
            realBinaryInstalled: true);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }

    [Fact(DisplayName = "An empty attestation file leaves a real binary unverified")]
    public async Task ResolveFromFileAsync_WithEmptyAttestationFile_ReportsUnverified()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"pf-attestation-{Guid.NewGuid():N}",
            "orcaslicer.sha256");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, string.Empty);

        SlicerBinaryIdentity identity = await SlicerBinaryAttestation.ResolveFromFileAsync(
            path,
            AttestedDigest,
            realBinaryInstalled: true,
            CancellationToken.None);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }

    [Fact(DisplayName = "An attestation file written by a verified build establishes the identity")]
    public async Task ResolveFromFileAsync_WithAttestedFile_ReportsPinnedIdentity()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"pf-attestation-{Guid.NewGuid():N}",
            "orcaslicer.sha256");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, AttestedDigest);

        SlicerBinaryIdentity identity = await SlicerBinaryAttestation.ResolveFromFileAsync(
            path,
            declaredSha256: null,
            realBinaryInstalled: true,
            CancellationToken.None);

        _ = identity.RealBinary.Should().BeTrue();
        _ = identity.BinarySha256.Should().Be(AttestedDigest);
    }

    [Fact(DisplayName = "A missing attestation file leaves the worker unverified")]
    public async Task ResolveFromFileAsync_WithMissingFile_ReportsUnverified()
    {
        SlicerBinaryIdentity identity = await SlicerBinaryAttestation.ResolveFromFileAsync(
            Path.Combine(Path.GetTempPath(), $"pf-missing-{Guid.NewGuid():N}", "orcaslicer.sha256"),
            AttestedDigest,
            realBinaryInstalled: true,
            CancellationToken.None);

        _ = identity.RealBinary.Should().BeFalse();
        _ = identity.BinarySha256.Should().BeNull();
    }
}
