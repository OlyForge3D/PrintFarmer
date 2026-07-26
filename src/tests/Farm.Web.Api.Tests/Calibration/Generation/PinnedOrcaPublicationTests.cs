using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Covers the immutable registry identity rules and the mandatory smoke gating decision that the
/// pinned OrcaSlicer publication workflow depends on.
/// </summary>
public sealed class PinnedOrcaPublicationTests
{
    private const string ValidDigest =
        "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c";

    private const string Repository = "ghcr.io/olyforge3d/printfarmer-orcaslicer-worker";

    [Fact(DisplayName = "A registry manifest digest is accepted only as sha256 plus 64 lowercase hex")]
    public void IsManifestDigest_AcceptsOnlyImmutableManifestDigests()
    {
        _ = PinnedOrcaPublication.IsManifestDigest(ValidDigest).Should().BeTrue();
        _ = PinnedOrcaPublication.IsManifestDigest(null).Should().BeFalse();
        _ = PinnedOrcaPublication.IsManifestDigest(string.Empty).Should().BeFalse();
        _ = PinnedOrcaPublication.IsManifestDigest(ValidDigest.ToUpperInvariant()).Should()
            .BeFalse("an uppercase digest is not the canonical registry form");
        _ = PinnedOrcaPublication.IsManifestDigest(ValidDigest[..^1]).Should()
            .BeFalse("a truncated digest cannot identify a manifest");
        _ = PinnedOrcaPublication.IsManifestDigest(ValidDigest + "a").Should().BeFalse();
        _ = PinnedOrcaPublication.IsManifestDigest(ValidDigest["sha256:".Length..]).Should()
            .BeFalse("the algorithm prefix is part of the registry identity");
        _ = PinnedOrcaPublication.IsManifestDigest("sha512:" + new string('a', 64)).Should().BeFalse();
        _ = PinnedOrcaPublication.IsManifestDigest("sha256:" + new string('g', 64)).Should().BeFalse();
        _ = PinnedOrcaPublication.IsManifestDigest("2.3.1").Should()
            .BeFalse("a mutable tag is never an immutable identity");
    }

    [Fact(DisplayName = "An immutable pull reference is built from a bare repository and its digest")]
    public void BuildImageReference_ProducesDigestPinnedReference() =>
        _ = PinnedOrcaPublication.BuildImageReference(Repository, ValidDigest).Should()
            .Be($"{Repository}@{ValidDigest}");

    [Theory(DisplayName = "A mutable or already-pinned repository is refused")]
    [InlineData("ghcr.io/olyforge3d/printfarmer-orcaslicer-worker:2.3.1")]
    [InlineData("ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:deadbeef")]
    [InlineData("   ")]
    public void BuildImageReference_RejectsUnusableRepositories(string repository) =>
        _ = FluentActions
            .Invoking(() => PinnedOrcaPublication.BuildImageReference(repository, ValidDigest))
            .Should().Throw<ArgumentException>();

    [Fact(DisplayName = "A tagged digest value is refused so nothing can be executed by tag")]
    public void BuildImageReference_RejectsNonManifestDigest() =>
        _ = FluentActions
            .Invoking(() => PinnedOrcaPublication.BuildImageReference(Repository, "latest"))
            .Should().Throw<ArgumentException>();

    [Fact(DisplayName = "A fully published image opens the smoke gate")]
    public void ResolveGate_WithPublishedDigest_CanRun()
    {
        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment(
            (PinnedOrcaPublication.ImageVariable, Repository),
            (PinnedOrcaPublication.ImageDigestVariable, ValidDigest),
            (PinnedOrcaPublication.SmokeModeVariable, PinnedOrcaPublication.RequiredSmokeMode)));

        _ = gate.CanRun.Should().BeTrue(gate.BlockReason);
        _ = gate.IsRequired.Should().BeTrue();
        _ = gate.ImageReference.Should().Be($"{Repository}@{ValidDigest}");
        _ = gate.Describe().Should().Contain(ValidDigest);
    }

    [Fact(DisplayName = "A missing publication blocks the gate with a concrete reason")]
    public void ResolveGate_WithoutImage_IsBlocked()
    {
        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment());

        _ = gate.CanRun.Should().BeFalse();
        _ = gate.IsRequired.Should().BeFalse();
        _ = gate.BlockReason.Should().Contain(PinnedOrcaPublication.ImageVariable);
        _ = FluentActions.Invoking(() => gate.ImageReference).Should()
            .Throw<InvalidOperationException>("a blocked gate must never hand out an image reference");
    }

    [Fact(DisplayName = "A published image without a digest blocks the gate")]
    public void ResolveGate_WithoutDigest_IsBlocked()
    {
        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment(
            (PinnedOrcaPublication.ImageVariable, Repository)));

        _ = gate.CanRun.Should().BeFalse();
        _ = gate.BlockReason.Should().Contain(PinnedOrcaPublication.ImageDigestVariable);
    }

    [Fact(DisplayName = "A tag presented as a digest blocks the gate")]
    public void ResolveGate_WithMutableDigest_IsBlocked()
    {
        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment(
            (PinnedOrcaPublication.ImageVariable, Repository),
            (PinnedOrcaPublication.ImageDigestVariable, "2.3.1")));

        _ = gate.CanRun.Should().BeFalse();
        _ = gate.BlockReason.Should().Contain("sha256:");
    }

    [Fact(DisplayName = "A blocked gate stays mandatory when the workflow requires the smoke")]
    public void ResolveGate_WhenRequiredButBlocked_StaysRequired()
    {
        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment(
            (PinnedOrcaPublication.SmokeModeVariable, "REQUIRED")));

        _ = gate.IsRequired.Should().BeTrue("the mode comparison must not depend on casing");
        _ = gate.CanRun.Should().BeFalse();
        _ = gate.Describe().Should().Contain("required=True");
    }

    [Fact(DisplayName = "An unset smoke mode leaves the gate advisory")]
    public void ResolveGate_WithoutSmokeMode_IsAdvisory() =>
        _ = PinnedOrcaPublication.ResolveGate(Environment(
                (PinnedOrcaPublication.ImageVariable, Repository),
                (PinnedOrcaPublication.ImageDigestVariable, ValidDigest)))
            .IsRequired.Should().BeFalse();

    private static Func<string, string?> Environment(params (string Name, string Value)[] values)
    {
        Dictionary<string, string> map = values.ToDictionary(
            entry => entry.Name,
            entry => entry.Value,
            StringComparer.Ordinal);
        return name => map.TryGetValue(name, out string? value) ? value : null;
    }
}
