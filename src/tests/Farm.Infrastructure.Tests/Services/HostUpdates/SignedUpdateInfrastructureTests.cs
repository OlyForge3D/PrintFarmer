using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class SignedUpdateInfrastructureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
    public void Parse_NonObjectOrMissingRequiredField_RejectsStrictJson()
    {
        Assert.Throws<JsonException>(() => SignedUpdateManifestValidator.Parse("[]"));
        string json = JsonSerializer.Serialize(CreateManifest("1.2.3", "stable", "main"), JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> fields = document.RootElement.EnumerateObject()
            .Where(property => property.Name != "platformDigests")
            .ToDictionary(property => property.Name, property => property.Value);
        Assert.Throws<JsonException>(() => SignedUpdateManifestValidator.Parse(JsonSerializer.Serialize(fields, JsonOptions)));
    }

    [Fact]
    public async Task Provider_MapsVerifiedManifestAndComputesDigestFromExactBytes()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        TestHandler handler = new(bytes);
        VerifiedGitHubReleaseMetadataProvider provider = new(new GitHubSignedReleaseDiscovery(new HttpClient(handler), new AcceptingVerifier()));

        SignedReleaseMetadata metadata = await provider.GetCurrentAsync("stable", default);

        Assert.Equal($"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}", metadata.Identity.ManifestDigest);
        Assert.Equal("stable:1.2.3", metadata.Identity.ReleaseId);
        Assert.Equal("stable:1.2.3", metadata.Identity.OciReleaseLabel);
        Assert.Equal("1.2.3", metadata.Identity.OciVersionLabel);
        Assert.Equal(manifest.BuildId, metadata.Identity.BuildMetadata);
        Assert.Equal("sha256:" + new string('a', 64), metadata.ComponentPlatformDigests["api/linux-amd64"]);
    }

    [Fact]
    public async Task Provider_ManifestDigestChangesWhenVerifiedBytesChange()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main");
        byte[] firstBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        byte[] secondBytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { BuildId = "build-2" }, JsonOptions);
        VerifiedGitHubReleaseMetadataProvider firstProvider = new(new GitHubSignedReleaseDiscovery(new HttpClient(new TestHandler(firstBytes)), new AcceptingVerifier()));
        VerifiedGitHubReleaseMetadataProvider secondProvider = new(new GitHubSignedReleaseDiscovery(new HttpClient(new TestHandler(secondBytes)), new AcceptingVerifier()));

        SignedReleaseMetadata first = await firstProvider.GetCurrentAsync("stable", default);
        SignedReleaseMetadata second = await secondProvider.GetCurrentAsync("stable", default);

        Assert.NotEqual(first.Identity.ManifestDigest, second.Identity.ManifestDigest);
        Assert.NotEqual(first.Identity.BuildMetadata, second.Identity.BuildMetadata);
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

    private sealed class AcceptingVerifier : ISignedReleaseVerifier
    {
        public Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class TestHandler(byte[] manifestBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri.Contains("/releases?", StringComparison.Ordinal) == true)
            {
                string releaseJson = JsonSerializer.Serialize(new[]
                {
                    new { id = 1L, tagName = "v1.2.3", draft = false, prerelease = false, assets = new[]
                    {
                        new { name = "update-manifest.json", browserDownloadUrl = "https://assets.test/manifest" },
                        new { name = "update-manifest.sigstore.json", browserDownloadUrl = "https://assets.test/bundle" },
                    } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) });
            }

            byte[] content = request.RequestUri?.AbsoluteUri.EndsWith("/manifest", StringComparison.Ordinal) == true
                ? manifestBytes
                : "{}"u8.ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
