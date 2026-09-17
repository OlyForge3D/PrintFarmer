using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class SignedUpdateInfrastructureTests
{
    [Theory]
    [InlineData("1.2.3", 1_002_003_000L)]
    [InlineData("1.2.3-insider.4", 1_002_003_004L)]
    public void DeriveSequence_CanonicalVersion_ReturnsDeterministicSequence(string version, long expected)
    {
        Assert.Equal(expected, SignedUpdateManifestValidator.DeriveSequence(version));
    }

    [Fact]
    public void Validate_ValidStableManifest_AcceptsStrictContract()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main");
        Assert.True(SignedUpdateManifestValidator.Validate(manifest).IsValid);
    }

    [Fact]
    public void Validate_UnknownDuplicateAndMutableServices_Rejects()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main") with
        {
            Services =
            [
                new("api", "ghcr.io/olyforge3d/printfarmer-api:latest"),
                new("api", "ghcr.io/olyforge3d/printfarmer-api@sha256:" + new string('a', 64)),
                new("frontend", "ghcr.io/olyforge3d/printfarmer-frontend@sha256:" + new string('a', 64)),
                new("slicer-host", "ghcr.io/olyforge3d/printfarmer-slicer-host@sha256:" + new string('a', 64)),
                new("printer-discovery", "ghcr.io/olyforge3d/printfarmer-printer-discovery@sha256:" + new string('a', 64)),
                new("orcaslicer-worker", "ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:" + new string('a', 64)),
            ],
        };
        Assert.Contains("service_set_invalid", SignedUpdateManifestValidator.Validate(manifest).Errors);
        Assert.Contains("image_reference_invalid", SignedUpdateManifestValidator.Validate(manifest).Errors);
    }

    [Fact]
    public void Parse_UnknownField_RejectsStrictJson()
    {
        string json = JsonSerializer.Serialize(CreateManifest("1.2.3", "stable", "main"))[..^1] + ",\"unexpected\":true}";
        Assert.Throws<JsonException>(() => SignedUpdateManifestValidator.Parse(json));
    }

    [Fact]
    public void Validate_SequenceChannelTagAndDigestTampering_Rejects()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main") with
        {
            Channel = "insider",
            Tag = "v9.9.9",
            Sequence = 1,
            PlatformDigests = new Dictionary<string, string> { ["linux-amd64"] = "sha256:bad" },
        };
        SignedUpdateValidationResult result = SignedUpdateManifestValidator.Validate(manifest);
        Assert.Contains("sequence_mismatch", result.Errors);
        Assert.Contains("tag_version_mismatch", result.Errors);
        Assert.Contains("platform_digest_invalid", result.Errors);
    }

    private static SignedUpdateManifest CreateManifest(string version, string channel, string branch)
    {
        string digest = "sha256:" + new string('a', 64);
        string[] platforms = ["linux-amd64"];
        string[] services = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];
        return new(1, $"v{version}", version, channel, branch, new string('b', 40), "build-1",
            SignedUpdateManifestValidator.DeriveSequence(version), true,
            services.Select(id => new SignedUpdateService(id, $"ghcr.io/olyforge3d/printfarmer-{id}@{digest}")).ToArray(),
            platforms, new Dictionary<string, string> { ["linux-amd64"] = digest });
    }
}
