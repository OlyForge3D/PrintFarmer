using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class SignedUpdateInfrastructureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DeriveSequence_GoldenSignerContract_MatchesValidInvalidOrderingAndDistinctCases()
    {
        SequenceGoldenFixture fixture = LoadSequenceFixture();

        foreach (SequenceValidCase testCase in fixture.ValidCases)
        {
            Assert.Equal(
                long.Parse(testCase.ExpectedSequence, System.Globalization.CultureInfo.InvariantCulture),
                SignedUpdateManifestValidator.DeriveSequence(testCase.Version));
        }

        foreach (SequenceInvalidCase testCase in fixture.InvalidCases)
        {
            if (!testCase.DeriveSequenceRejects)
            {
                continue;
            }

            Assert.ThrowsAny<Exception>(() => SignedUpdateManifestValidator.DeriveSequence(testCase.Version));
        }

        foreach (SequenceOrderingCase testCase in fixture.Ordering)
        {
            Assert.True(
                SignedUpdateManifestValidator.DeriveSequence(testCase.Lower)
                < SignedUpdateManifestValidator.DeriveSequence(testCase.Higher),
                testCase.Name);
        }

        foreach (SequenceDistinctGroup group in fixture.DistinctGroups)
        {
            long[] sequences = group.Versions
                .Select(SignedUpdateManifestValidator.DeriveSequence)
                .ToArray();
            Assert.Equal(sequences.Length, sequences.Distinct().Count());
        }
    }

    [Fact]
    public void DeriveSequence_GoldenFixture_MatchesSchemaVersionPattern()
    {
        string schema = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts", "ci", "fixtures", "release-version-sequence.schema.json"));
        using JsonDocument schemaDocument = JsonDocument.Parse(schema);
        string validCaseRef = schemaDocument.RootElement
            .GetProperty("properties")
            .GetProperty("validCases")
            .GetProperty("items")
            .GetProperty("$ref")
            .GetString()!;
        Assert.Equal("#/$defs/validCase", validCaseRef);
        string versionSyntax = schemaDocument.RootElement
            .GetProperty("properties")
            .GetProperty("contract")
            .GetProperty("properties")
            .GetProperty("versionSyntax")
            .GetProperty("const")
            .GetString()!;
        Assert.Equal("MAJOR.MINOR.PATCH[-insider.SEQUENCE]", versionSyntax);

        SequenceGoldenFixture fixture = LoadSequenceFixture();
        foreach (SequenceValidCase testCase in fixture.ValidCases)
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.Version));
        }

        Assert.Contains(fixture.InvalidCases, testCase => testCase.Version == "01.2.3" && testCase.DeriveSequenceRejects);
        Assert.Contains(fixture.InvalidCases, testCase => testCase.Version == "1.02.3" && testCase.DeriveSequenceRejects);
        Assert.Contains(fixture.InvalidCases, testCase => testCase.Version == "1.2.03" && testCase.DeriveSequenceRejects);
    }

    [Fact]
    public void DeriveSequence_GoldenFixture_ContractLimitsAndRadicesMatchImplementation()
    {
        // Guards against the schema's documented contract (limits/radices/formula "const"
        // values) silently drifting from SignedUpdateManifestValidator's actual encoding. The
        // schema is itself a JSON Schema document, so each documented value lives at
        // properties.contract.properties.<group>.properties.<name>.const, not as plain data.
        string schema = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts", "ci", "fixtures", "release-version-sequence.schema.json"));
        using JsonDocument schemaDocument = JsonDocument.Parse(schema);
        JsonElement contractProperties = schemaDocument.RootElement
            .GetProperty("properties")
            .GetProperty("contract")
            .GetProperty("properties");
        JsonElement radices = contractProperties.GetProperty("radices").GetProperty("properties");
        JsonElement limits = contractProperties.GetProperty("limits").GetProperty("properties");

        static long Const(JsonElement group, string name) => group.GetProperty(name).GetProperty("const").GetInt64();

        Assert.Equal(SignedUpdateManifestValidator.SequenceMinorMaximum + 1, Const(radices, "minor"));
        Assert.Equal(SignedUpdateManifestValidator.SequencePatchMaximum + 1, Const(radices, "patch"));
        Assert.Equal(SignedUpdateManifestValidator.SequenceStableSuffix + 1, Const(radices, "suffix"));

        Assert.Equal(SignedUpdateManifestValidator.SequenceMajorMaximum, Const(limits, "major"));
        Assert.Equal(SignedUpdateManifestValidator.SequenceMinorMaximum, Const(limits, "minor"));
        Assert.Equal(SignedUpdateManifestValidator.SequencePatchMaximum, Const(limits, "patch"));
        Assert.Equal(SignedUpdateManifestValidator.SequenceInsiderMaximum, Const(limits, "insiderSequence"));
        Assert.Equal(SignedUpdateManifestValidator.SequenceStableSuffix, Const(limits, "stableSuffix"));

        // Sanity-check the documented formula itself against a golden case, using the schema's
        // own radices rather than re-deriving them, so the assertion fails if either drifts.
        SequenceGoldenFixture fixture = LoadSequenceFixture();
        long minorRadix = Const(radices, "minor");
        long patchRadix = Const(radices, "patch");
        long suffixRadix = Const(radices, "suffix");
        foreach (SequenceValidCase testCase in fixture.ValidCases)
        {
            long expected = long.Parse(testCase.ExpectedSequence, System.Globalization.CultureInfo.InvariantCulture);
            long recomputed = (((testCase.Parsed.Major * minorRadix) + testCase.Parsed.Minor) * patchRadix + testCase.Parsed.Patch) * suffixRadix + testCase.Parsed.Suffix;
            Assert.Equal(expected, recomputed);
        }
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
    public void Parse_ProducerGoldenFixture_PreservesLfAndExpectedSequence()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(),
            "scripts", "ci", "fixtures", "update-manifest.golden.json"));

        Assert.DoesNotContain((byte)'\r', bytes);
        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(bytes));

        Assert.True(SignedUpdateManifestValidator.Validate(manifest).IsValid);
        Assert.Equal(10020000300042, manifest.Sequence);
        Assert.Equal("0.0.0", manifest.MinimumUpdaterVersion);
    }

    [Fact]
    public void Parse_MissingMinimumUpdaterVersion_Rejects()
    {
        string json = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "ci", "fixtures", "update-manifest.golden.json"))
            .Replace(",\"minimumUpdaterVersion\":\"0.0.0\"", string.Empty, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => SignedUpdateManifestValidator.Parse(json));
    }

    [Fact]
    public void Parse_NestedPlatformDigestMap_Rejects()
    {
        string json = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "ci", "fixtures", "update-manifest.golden.json"))
            .Replace("\"api/linux-amd64\":\"sha256:", "\"api\":{\"linux-amd64\":\"sha256:", StringComparison.Ordinal)
            .Replace(new string('0', 64) + "\",\"api/linux-arm64\"", new string('0', 64) + "\"},\"api/linux-arm64\"", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => SignedUpdateManifestValidator.Parse(json));
    }

    [Fact]
    public void Validate_WrongTopLevelPlatformEntries_Rejects()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main") with
        {
            Platforms = ["linux-amd64", "linux-arm64", "windows-amd64"],
        };

        Assert.Contains("platform_invalid", SignedUpdateManifestValidator.Validate(manifest).Errors);
    }

    [Theory]
    [InlineData("""{"id":"api","image":"ghcr.io/olyforge3d/printfarmer-api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","extra":true}""")]
    [InlineData("""{"id":"api","image":null}""")]
    public void Parse_InvalidNestedService_RejectsStrictJson(string service)
    {
        string json = JsonSerializer.Serialize(CreateManifest("1.2.3", "stable", "main"), JsonOptions);
        json = json.Replace("\"services\":[", $"\"services\":[{service},", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => SignedUpdateManifestValidator.Parse(json));
    }

    [Fact]
    public async Task Provider_MapsVerifiedManifestAndComputesDigestFromExactBytes()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main");
        Dictionary<string, string> childDigests = manifest.PlatformDigests.ToDictionary(StringComparer.Ordinal);
        childDigests["api/linux-amd64"] = "sha256:" + new string('b', 64);
        manifest = manifest with { PlatformDigests = childDigests };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        TestHandler handler = new(bytes);
        VerifiedGitHubReleaseMetadataProvider provider = new(new GitHubSignedReleaseDiscovery(new HttpClient(handler), new AcceptingVerifier()));

        SignedReleaseMetadata metadata = await provider.GetCurrentAsync("stable", default);

        Assert.Equal($"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()}", metadata.Identity.ManifestDigest);
        Assert.Equal("stable:1.2.3", metadata.Identity.ReleaseId);
        Assert.Equal("stable:1.2.3", metadata.Identity.OciReleaseLabel);
        Assert.Equal("1.2.3", metadata.Identity.OciVersionLabel);
        Assert.Equal(manifest.BuildId, metadata.Identity.BuildMetadata);
        Assert.Equal(manifest.Sequence, metadata.Sequence);
        Assert.Equal("sha256:" + new string('b', 64), metadata.ComponentPlatformDigests["api/linux-amd64"]);
        Assert.Equal("sha256:" + new string('a', 64), metadata.ComponentIndexDigests!["api"]);
        Assert.Equal(["linux-amd64", "linux-arm64"], metadata.ComponentPlatforms!["api"]);
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
            PlatformDigests = new Dictionary<string, string> { ["api/linux-amd64"] = "sha256:bad" },
        };
        SignedUpdateValidationResult result = SignedUpdateManifestValidator.Validate(manifest);
        Assert.Contains("sequence_mismatch", result.Errors);
        Assert.Contains("tag_version_mismatch", result.Errors);
        Assert.Contains("platform_digest_invalid", result.Errors);
    }

    [Fact]
    public void Validate_IncompleteOrMismatchedServicePlatformDigestMap_Rejects()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3", "stable", "main");
        Dictionary<string, string> incomplete = manifest.PlatformDigests
            .Where(pair => pair.Key != "api/linux-amd64")
            .ToDictionary(StringComparer.Ordinal);
        Dictionary<string, string> mismatched = incomplete.ToDictionary(StringComparer.Ordinal);
        mismatched["unknown/linux-amd64"] = "sha256:" + new string('a', 64);

        Assert.Contains(
            "platform_digest_invalid",
            SignedUpdateManifestValidator.Validate(manifest with { PlatformDigests = incomplete }).Errors);
        Assert.Contains(
            "platform_digest_invalid",
            SignedUpdateManifestValidator.Validate(manifest with { PlatformDigests = mismatched }).Errors);
    }

    [Fact]
    public async Task Discovery_PaginatesAndOnlyOrdersVerifiedExactStableTags()
    {
        SignedUpdateManifest first = CreateManifest("1.2.3", "stable", "main");
        SignedUpdateManifest latest = CreateManifest("2.0.0", "stable", "main");
        string firstJson = JsonSerializer.Serialize(first, JsonOptions);
        string latestJson = JsonSerializer.Serialize(latest, JsonOptions);
        string[] drafts = Enumerable.Range(1, 99).Select(index =>
            $$"""{"id":{{index}},"tagName":"v99.0.0","draft":true,"prerelease":false,"assets":[]}""").ToArray();
        string pageOne = "[" + string.Join(',', drafts.Append(ReleaseJson(100, "v1.2.3", false, false, true))) + "]";
        string pageTwo = "[" + string.Join(',', new[]
        {
            ReleaseJson(101, "v2.0.0-insider.9", false, false, true),
            ReleaseJson(102, "v9.0.0", false, false, false),
            ReleaseJson(103, "v3.0.0", false, false, true),
            ReleaseJson(104, "v2.0.0", false, false, true),
        }) + "]";
        ReleaseHandler handler = new(new Dictionary<int, string> { [1] = pageOne, [2] = pageTwo },
            new Dictionary<long, byte[]> { [1001] = System.Text.Encoding.UTF8.GetBytes(firstJson), [1031] = System.Text.Encoding.UTF8.GetBytes(firstJson), [1041] = System.Text.Encoding.UTF8.GetBytes(latestJson) });
        RecordingVerifier verifier = new(manifest => manifest.Span.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(latestJson)));
        GitHubSignedReleaseDiscovery discovery = new(new HttpClient(handler), verifier);

        VerifiedSignedUpdateRelease? result = await discovery.DiscoverAsync("stable", default);

        Assert.NotNull(result);
        Assert.Equal("2.0.0", result.Manifest.Version);
        Assert.Equal(
            ["https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/main"],
            verifier.Identities);
        Assert.Equal(new[] { 1, 2 }, handler.ReleasePages);
    }

    [Fact]
    public async Task Discovery_UntrustedBrowserUrlAndRedirectOrigin_NeverEscapesPinnedGitHubAssetEndpoint()
    {
        List<Uri> requests = [];
        DelegateHandler handler = new(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsoluteUri.Contains("/releases?", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"[{ReleaseJson(7, "v1.2.3", false, false, true)}]"),
                };
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://attacker.invalid/release-asset");
            return redirect;
        });
        RecordingVerifier verifier = new(_ => true);

        VerifiedSignedUpdateRelease? result = await new GitHubSignedReleaseDiscovery(
            new HttpClient(handler),
            verifier).DiscoverAsync("stable", default);

        Assert.Null(result);
        Assert.Empty(verifier.Identities);
        Assert.Equal(
            [
                "https://api.github.com/repos/OlyForge3D/PrintFarmer/releases?per_page=100&page=1",
                "https://api.github.com/repos/OlyForge3D/PrintFarmer/releases/assets/71",
            ],
            requests.Select(uri => uri.AbsoluteUri));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.SeeOther)]
    public async Task Discovery_AllowedPermanentAndSeeOtherRedirects_AreBoundedAndAccepted(
        HttpStatusCode redirectStatus)
    {
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(
            CreateManifest("1.2.3", "stable", "main"),
            JsonOptions);
        DelegateHandler handler = new(request =>
        {
            string uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("/releases?", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"[{ReleaseJson(7, "v1.2.3", false, false, true)}]"),
                };
            }

            if (request.RequestUri.IdnHost == "api.github.com")
            {
                var redirect = new HttpResponseMessage(redirectStatus);
                redirect.Headers.Location = new Uri(
                    $"https://objects.githubusercontent.com/{request.RequestUri.Segments[^1]}");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(uri.EndsWith("/71", StringComparison.Ordinal)
                    ? manifest
                    : "{}"u8.ToArray()),
            };
        });

        VerifiedSignedUpdateRelease? result = await new GitHubSignedReleaseDiscovery(
            new HttpClient(handler),
            new AcceptingVerifier()).DiscoverAsync("stable", default);

        Assert.Equal("1.2.3", result!.Manifest.Version);
    }

    [Fact]
    public async Task Discovery_OversizedManifest_IsRejectedWithoutVerification()
    {
        byte[] oversized = new byte[(256 * 1024) + 1];
        DelegateHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/releases?", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"[{ReleaseJson(8, "v1.2.3", false, false, true)}]"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(oversized),
            };
        });
        RecordingVerifier verifier = new(_ => true);

        VerifiedSignedUpdateRelease? result = await new GitHubSignedReleaseDiscovery(
            new HttpClient(handler),
            verifier).DiscoverAsync("stable", default);

        Assert.Null(result);
        Assert.Empty(verifier.Identities);
    }

    [Fact]
    public async Task Discovery_OversizedReleaseListing_FailsClosedBeforeAssetRequests()
    {
        byte[] oversized = new byte[(4 * 1024 * 1024) + 1];
        DelegateHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized),
        });

        Func<Task> act = async () => await new GitHubSignedReleaseDiscovery(
            new HttpClient(handler),
            new AcceptingVerifier()).DiscoverAsync("stable", default);

        await Assert.ThrowsAsync<InvalidDataException>(act);
    }

    [Fact]
    public async Task Discovery_BadCandidateHttpAndOverflow_DoNotAbortLaterValidCandidate()
    {
        SignedUpdateManifest valid = CreateManifest("2.0.0", "stable", "main");
        string overflow = JsonSerializer.Serialize(
            CreateManifest("1.0.0", "stable", "main") with
            {
                Version = "100.0.0",
                Tag = "v100.0.0",
            },
            JsonOptions);
        byte[] validBytes = JsonSerializer.SerializeToUtf8Bytes(valid, JsonOptions);
        int failedAssetRequests = 0;
        DelegateHandler handler = new(request =>
        {
            string uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("/releases?", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"[{ReleaseJson(1, "v0.1.0", false, false, true)},{ReleaseJson(2, "v100.0.0", false, false, true)},{ReleaseJson(3, "v2.0.0", false, false, true)}]"),
                };
            }

            if (uri.EndsWith("/11", StringComparison.Ordinal))
            {
                failedAssetRequests++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (uri.EndsWith("/21", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(overflow),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(uri.EndsWith("/31", StringComparison.Ordinal)
                    ? validBytes
                    : "{}"u8.ToArray()),
            };
        });

        VerifiedSignedUpdateRelease? release = await new GitHubSignedReleaseDiscovery(
            new HttpClient(handler),
            new AcceptingVerifier()).DiscoverAsync("stable", default);

        Assert.Equal(1, failedAssetRequests);
        Assert.Equal("2.0.0", release!.Manifest.Version);
    }

    [Fact]
    public async Task Discovery_OversizedBundle_IsRejectedWithoutVerification()
    {
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(
            CreateManifest("1.2.3", "stable", "main"),
            JsonOptions);
        byte[] oversizedBundle = new byte[(1024 * 1024) + 1];
        DelegateHandler handler = new(request =>
        {
            string uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("/releases?", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"[{ReleaseJson(9, "v1.2.3", false, false, true)}]"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(uri.EndsWith("/91", StringComparison.Ordinal)
                    ? manifest
                    : oversizedBundle),
            };
        });
        RecordingVerifier verifier = new(_ => true);

        VerifiedSignedUpdateRelease? result = await new GitHubSignedReleaseDiscovery(
            new HttpClient(handler),
            verifier).DiscoverAsync("stable", default);

        Assert.Null(result);
        Assert.Empty(verifier.Identities);
    }

    [Fact]
    public async Task Discovery_OnlyAcceptsExactInsiderTagAndIdentity()
    {
        SignedUpdateManifest manifest = CreateManifest("1.2.3-insider.4", "insider", "development");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        ReleaseHandler handler = new(new Dictionary<int, string>
        {
            [1] = "[" + string.Join(',', new[]
            {
                ReleaseJson(1, "v1.2.3", false, true, true),
                ReleaseJson(2, "v1.2.3-insider.4", false, true, true),
            }) + "]",
        }, new Dictionary<long, byte[]> { [11] = bytes, [21] = bytes });
        RecordingVerifier verifier = new(_ => true);

        VerifiedSignedUpdateRelease? result = await new GitHubSignedReleaseDiscovery(new HttpClient(handler), verifier).DiscoverAsync("insider", default);

        Assert.NotNull(result);
        Assert.Equal("1.2.3-insider.4", result.Manifest.Version);
        Assert.Equal(["https://github.com/OlyForge3D/PrintFarmer/.github/workflows/consolidated-release.yml@refs/heads/development"], verifier.Identities);
    }

    [Fact]
    public async Task CosignVerifier_UsesExactArgumentListAndCleansTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        RecordingRunner runner = new();
        ProcessCosignVerifier verifier = new(new("cosign-path", TimeSpan.FromSeconds(2), 9), runner, () =>
        {
            Directory.CreateDirectory(directory);
            return directory;
        });

        Assert.True(await verifier.VerifyAsync("manifest"u8.ToArray(), "bundle"u8.ToArray(), "identity", default));

        Assert.False(Directory.Exists(directory));
        Assert.Equal("cosign-path", runner.Command!.ExecutablePath);
        Assert.Equal(["verify-blob", "--bundle", Path.Combine(directory, "bundle.json"), "--certificate-oidc-issuer",
            "https://token.actions.githubusercontent.com", "--certificate-identity", "identity", Path.Combine(directory, "manifest.json")], runner.Command.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(2), runner.Timeout);
        Assert.Equal(9, runner.MaxDiagnostics);
    }

    [Fact]
    public async Task CosignVerifier_TimeoutReturnsFalseButCallerCancellationPropagates()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        ProcessCosignVerifier verifier = new(new("cosign", TimeSpan.FromSeconds(1)), new CancellingRunner(), () =>
        {
            Directory.CreateDirectory(directory);
            return directory;
        });

        Assert.False(await verifier.VerifyAsync(Array.Empty<byte>(), Array.Empty<byte>(), "identity", default));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Array.Empty<byte>(), Array.Empty<byte>(), "identity", cancellation.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ProcessCosignRunner_OutputDrainTimeout_IsBoundedAndObservesLaterFault()
    {
        Task<string[]> drain = Task.Run(async () =>
        {
            await Task.Delay(100);
            throw new IOException("late pipe failure");
#pragma warning disable CS0162
            return Array.Empty<string>();
#pragma warning restore CS0162
        });

#pragma warning disable VSTHRD003 // drain is deliberately controlled by this test to outlive the bounded wait.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessCosignRunner.DrainOutputAsync(drain, TimeSpan.FromMilliseconds(20)));

        await Assert.ThrowsAsync<IOException>(() => drain);
#pragma warning restore VSTHRD003
    }

    [Fact]
    public void Parse_GeneratedSignerManifest_ConsumesCompositePlatformContract()
    {
        string json = JsonSerializer.Serialize(CreateManifest("1.0.0", "stable", "main"), JsonOptions);

        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(json);
        SignedUpdateValidationResult validation = SignedUpdateManifestValidator.Validate(manifest);

        Assert.True(validation.IsValid, string.Join(',', validation.Errors));
        Assert.Equal(100_000_000_99999, manifest.Sequence);
        Assert.Equal(["linux-amd64", "linux-arm64"], manifest.Platforms);
        Assert.Equal(["linux-amd64", "linux-arm64"], manifest.Services[0].Platforms);
        Assert.Equal(["linux-amd64"], manifest.Services[4].Platforms);
        Assert.Contains("printer-discovery/linux-amd64", manifest.PlatformDigests.Keys);
    }

    private static SignedUpdateManifest CreateManifest(string version, string channel, string branch)
    {
        string digest = "sha256:" + new string('a', 64);
        string[] platforms = ["linux-amd64", "linux-arm64"];
        string[] services = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];
        return new(1, $"v{version}", version, channel, branch, new string('b', 40), "build-1",
            SignedUpdateManifestValidator.DeriveSequence(version), true,
            services.Select(id => new SignedUpdateService(
                id,
                $"ghcr.io/olyforge3d/printfarmer-{id}@{digest}",
                id == "orcaslicer-worker" ? ["linux-amd64"] : platforms)).ToArray(),
            platforms,
            services.SelectMany(id => (id == "orcaslicer-worker" ? new[] { "linux-amd64" } : platforms)
                .Select(platform => $"{id}/{platform}"))
                .ToDictionary(key => key, _ => digest, StringComparer.Ordinal),
            "0.0.0");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VERSION")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static SequenceGoldenFixture LoadSequenceFixture()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VERSION")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string path = Path.Combine(
            root.FullName,
            "scripts",
            "ci",
            "fixtures",
            "release-version-sequence.golden.json");
        return JsonSerializer.Deserialize<SequenceGoldenFixture>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed record SequenceGoldenFixture(
        IReadOnlyList<SequenceValidCase> ValidCases,
        IReadOnlyList<SequenceInvalidCase> InvalidCases,
        IReadOnlyList<SequenceOrderingCase> Ordering,
        IReadOnlyList<SequenceDistinctGroup> DistinctGroups);
    private sealed record SequenceValidCase(string Name, string Version, SequenceParsed Parsed, string ExpectedSequence);
    private sealed record SequenceParsed(long Major, long Minor, long Patch, string Kind, long Suffix);
    private sealed record SequenceInvalidCase(string Name, string Version, string ErrorContains, bool DeriveSequenceRejects = true);
    private sealed record SequenceOrderingCase(string Name, string Lower, string Higher);
    private sealed record SequenceDistinctGroup(string Name, IReadOnlyList<string> Versions);

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
                        new { id = 11L, name = "update-manifest.json", browserDownloadUrl = "http://169.254.169.254/latest/meta-data" },
                        new { id = 12L, name = "update-manifest.sigstore.json", browserDownloadUrl = "https://attacker.invalid/bundle" },
                    } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) });
            }

            byte[] content = request.RequestUri?.AbsoluteUri.EndsWith("/11", StringComparison.Ordinal) == true
                ? manifestBytes
                : "{}"u8.ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private static string ReleaseJson(int id, string tag, bool draft, bool prerelease, bool assets)
    {
        string assetJson = assets
            ? $"[{{\"id\":{id}1,\"name\":\"update-manifest.json\",\"browserDownloadUrl\":\"http://169.254.169.254/manifest-{id}\"}},{{\"id\":{id}2,\"name\":\"update-manifest.sigstore.json\",\"browserDownloadUrl\":\"https://attacker.invalid/bundle-{id}\"}}]"
            : "[]";
        return $"{{\"id\":{id},\"tagName\":\"{tag}\",\"draft\":{draft.ToString().ToLowerInvariant()},\"prerelease\":{prerelease.ToString().ToLowerInvariant()},\"assets\":{assetJson}}}";
    }

    private sealed class ReleaseHandler(Dictionary<int, string> releasePages, Dictionary<long, byte[]> assets) : HttpMessageHandler
    {
        public List<int> ReleasePages { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsoluteUri;
            if (path.Contains("/releases?", StringComparison.Ordinal))
            {
                string pageParameter = request.RequestUri.Query.TrimStart('?').Split('&')
                    .Single(pair => pair.StartsWith("page=", StringComparison.Ordinal));
                int page = int.Parse(pageParameter["page=".Length..]);
                ReleasePages.Add(page);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releasePages.GetValueOrDefault(page, "[]")) });
            }

            long key = long.Parse(path[(path.LastIndexOf('/') + 1)..]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assets.GetValueOrDefault(key, "{}"u8.ToArray())) });
        }
    }

    private sealed class RecordingVerifier(Func<ReadOnlyMemory<byte>, bool> accept) : ISignedReleaseVerifier
    {
        public List<string> Identities { get; } = [];

        public Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
        {
            Identities.Add(certificateIdentity);
            return Task.FromResult(accept(manifest));
        }

    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class RecordingRunner : ICosignProcessRunner
    {
        public CosignProcessCommand? Command { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public int MaxDiagnostics { get; private set; }

        public Task<CosignProcessResult> RunAsync(CosignProcessCommand command, TimeSpan timeout, int maxDiagnostics, CancellationToken cancellationToken)
        {
            Command = command;
            Timeout = timeout;
            MaxDiagnostics = maxDiagnostics;
            return Task.FromResult(new CosignProcessResult(0, "untrusted tool diagnostics"));
        }
    }

    private sealed class CancellingRunner : ICosignProcessRunner
    {
        public Task<CosignProcessResult> RunAsync(CosignProcessCommand command, TimeSpan timeout, int maxDiagnostics, CancellationToken cancellationToken) =>
            Task.FromCanceled<CosignProcessResult>(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
    }
}
