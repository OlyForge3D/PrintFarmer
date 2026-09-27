using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>Issue #3116: the daemon verifies signed releases itself and fails closed, independent of the API.</summary>
public sealed class HostUpdateDaemonReleaseVerifierTests : IDisposable
{
    private const string Tag = "v1.2.3-insider.42";
    private const string ReleaseId = "insider:1.2.3-insider.42";
    private const long Sequence = 10020000300042;
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly string[] Amd64Digests =
    [
        "sha256:" + new string('0', 64),
        "sha256:" + new string('2', 64),
        "sha256:" + new string('4', 64),
        "sha256:" + new string('6', 64),
        "sha256:" + new string('8', 64),
        "sha256:" + new string('9', 64),
    ];

    private readonly DirectoryInfo root = HostStateTestPaths.CreateTempSubdirectory("pf-daemon-verify-");
    private readonly DateTimeOffset now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly byte[] manifest;
    private readonly string manifestDigest;
    private readonly string trustedRootPath;
    private readonly string cosignPath;
    private readonly FakeSource source;
    private readonly RecordingVerifier signature = new();
    private readonly FileHostUpdateDaemonVerificationJournal journal;
    private readonly FixedTime time;

    public HostUpdateDaemonReleaseVerifierTests()
    {
        manifest = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "scripts", "ci", "fixtures", "update-manifest.golden.json"));
        manifestDigest = Digest(manifest);
        trustedRootPath = Path.Combine(root.FullName, "trusted_root.json");
        File.WriteAllText(trustedRootPath, TrustedRoot("2020-01-01T00:00:00Z", null));
        cosignPath = Path.Combine(root.FullName, "cosign");
        source = new FakeSource(new HostUpdateDaemonSignedArtifacts(manifest, "{\"bundle\":true}"u8.ToArray()));
        journal = new FileHostUpdateDaemonVerificationJournal(Path.Combine(root.FullName, "state", FileHostUpdateDaemonVerificationJournal.FileName));
        time = new FixedTime(now);
    }

    public void Dispose()
    {
        try
        {
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task ValidSignedRelease_IsVerified_BoundToExactImageSet_AndJournaled()
    {
        HostUpdateDaemonVerificationResult result = await Verifier().VerifyAsync(Approved(), Context(), default);

        result.Verified.Should().BeTrue(result.RefusalCode);
        HostUpdateDaemonVerifiedRelease release = result.Release!;
        release.ReleaseId.Should().Be(ReleaseId);
        release.Channel.Should().Be("insider");
        release.Sequence.Should().Be(Sequence);
        release.ManifestDigest.Should().Be(manifestDigest);
        release.SourceCommit.Should().Be(SourceCommit);
        release.HostPlatform.Should().Be("linux-amd64");
        release.ExpiresAt.Should().Be(now.AddMinutes(4));
        release.Targets.Select(t => t.ChildDigest).Should().Equal(Amd64Digests);
        release.Targets.Should().OnlyContain(t => t.Platform == "linux-amd64");
        release.Matches(RequestFor(release)).Should().BeTrue();

        source.Requests.Should().Equal(("insider", Tag));
        signature.Identities.Should().Equal(HostUpdateTrustRoot.CertificateIdentity("insider"));
        signature.Options.Should().ContainSingle();
        signature.Options[0].ExecutablePath.Should().Be(cosignPath);
        signature.Options[0].TrustedRootPath.Should().NotBe(trustedRootPath);
        File.Exists(signature.Options[0].TrustedRootPath!).Should().BeFalse("the private trusted-root copy is removed after verification");
        signature.RootCopies.Should().ContainSingle().Which.Should().Equal(File.ReadAllBytes(trustedRootPath));

        HostUpdateDaemonVerificationEvidence evidence = journal.ReadAll().Should().ContainSingle().Subject;
        evidence.Outcome.Should().Be(HostUpdateDaemonVerificationEvidence.VerifiedOutcome);
        evidence.Code.Should().Be("verified");
        evidence.ReleaseId.Should().Be(ReleaseId);
        evidence.ManifestDigest.Should().Be(manifestDigest);
        evidence.SourceCommit.Should().Be(SourceCommit);
        evidence.TrustRoot.Should().Be(HostUpdateTrustRoot.DefaultTrustRoot);
        evidence.TrustRootFingerprint.Should().Be(HostUpdateTrustRoot.Fingerprint);
    }

    [Fact]
    public async Task TamperedManifest_IsRefusedBeforeSignatureVerification()
    {
        byte[] tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(Amd64Digests[0], "sha256:" + new string('f', 64), StringComparison.Ordinal));
        source.Artifacts = new HostUpdateDaemonSignedArtifacts(tampered, "{}"u8.ToArray());

        await ExpectRefusalAsync(Approved(), "manifest_digest_mismatch");
        signature.Identities.Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestWhoseDigestMatchesButSignatureDoesNot_IsRefused()
    {
        signature.Result = false;

        await ExpectRefusalAsync(Approved(), "signature_invalid");
    }

    [Fact]
    public async Task SignatureVerifierFailure_IsRefused()
    {
        signature.Throw = new InvalidOperationException("cosign crashed");

        await ExpectRefusalAsync(Approved(), "signature_unverifiable");
    }

    [Fact]
    public async Task MissingRelease_IsRefused()
    {
        source.Artifacts = null;

        await ExpectRefusalAsync(Approved(), "manifest_missing");
    }

    [Fact]
    public async Task MissingSignatureBundle_IsRefused()
    {
        source.Artifacts = new HostUpdateDaemonSignedArtifacts(manifest, []);

        await ExpectRefusalAsync(Approved(), "signature_missing");
        signature.Identities.Should().BeEmpty();
    }

    [Fact]
    public async Task OversizedArtifacts_AreRefused()
    {
        source.Artifacts = new HostUpdateDaemonSignedArtifacts(new byte[HostUpdateDaemonReleaseVerifier.MaxManifestBytes + 1], "{}"u8.ToArray());
        await ExpectRefusalAsync(Approved(), "manifest_oversized");

        source.Artifacts = new HostUpdateDaemonSignedArtifacts(manifest, new byte[HostUpdateDaemonReleaseVerifier.MaxBundleBytes + 1]);
        await ExpectRefusalAsync(Approved(), "signature_oversized");
    }

    [Fact]
    public async Task UnavailableReleaseSource_IsRefused()
    {
        source.Throw = new HttpRequestException("offline");

        await ExpectRefusalAsync(Approved(), "release_source_unavailable");
    }

    [Fact]
    public async Task HostChannelDifferentFromApproval_IsRefusedBeforeFetching()
    {
        await ExpectRefusalAsync(Approved(), "channel_mismatch", Context() with { HostChannel = "stable" });
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CrossChannelManifest_IsRefused_EvenWhenSignedByTheRequestedChannelIdentity()
    {
        // A stable approval naming the insider manifest's exact bytes must not verify as stable.
        HostUpdateDaemonApprovedRelease approved = Approved() with { Channel = "stable", ReleaseId = "stable:1.2.3" };

        await ExpectRefusalAsync(approved, "channel_mismatch", Context() with { HostChannel = "stable" });
        signature.Identities.Should().Equal(HostUpdateTrustRoot.CertificateIdentity("stable"));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("release")]
    public async Task ApprovalThatDoesNotMatchSignedManifest_IsRefused(string field)
    {
        HostUpdateDaemonApprovedRelease approved = field == "sequence"
            ? Approved() with { Sequence = Sequence - 1 }
            : Approved() with { ReleaseId = "insider:1.2.3-insider.43" };

        await ExpectRefusalAsync(approved, "manifest_binding_mismatch");
    }

    [Fact]
    public async Task InvalidManifestContent_IsRefused()
    {
        byte[] invalid = "{\"schema\":1}"u8.ToArray();
        source.Artifacts = new HostUpdateDaemonSignedArtifacts(invalid, "{}"u8.ToArray());

        await ExpectRefusalAsync(Approved() with { ManifestDigest = Digest(invalid) }, "manifest_invalid");
    }

    [Fact]
    public async Task HostPlatformWithoutCompleteImageSet_IsRefused()
    {
        await ExpectRefusalAsync(Approved(), "image_set_incomplete:orcaslicer-worker", Context() with { HostPlatform = "linux-arm64" });
    }

    [Theory]
    [InlineData("stable-ish", "linux-amd64")]
    [InlineData("insider", "../linux-amd64")]
    [InlineData("insider", "")]
    public async Task InvalidHostPolicy_IsRefused(string channel, string platform)
    {
        await ExpectRefusalAsync(Approved(), "host_policy_invalid", Context() with { HostChannel = channel, HostPlatform = platform });
    }

    [Fact]
    public async Task ExpiredApproval_IsRefused()
    {
        await ExpectRefusalAsync(Approved() with { ExpiresAt = now }, "approval_expired");
        source.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ApprovalBeyondFiveMinuteLifetime_IsRefused()
    {
        await ExpectRefusalAsync(Approved() with { ExpiresAt = now.AddMinutes(5).AddSeconds(1) }, "approval_lifetime_exceeded");
    }

    [Theory]
    [InlineData("../etc/passwd", "sha256:" + "ab", "insider")]
    [InlineData("approval-1", "sha256:zz", "insider")]
    [InlineData("approval-1", null, "beta")]
    public async Task MalformedApproval_IsRefused_AndEvidenceIsRedacted(string approvalId, string? digest, string channel)
    {
        HostUpdateDaemonApprovedRelease approved = Approved() with { ApprovalId = approvalId, Channel = channel };
        if (digest is not null)
        {
            approved = approved with { ManifestDigest = digest };
        }

        await ExpectRefusalAsync(approved, "approval_invalid");
        HostUpdateDaemonVerificationEvidence evidence = journal.ReadAll().Should().ContainSingle().Subject;
        if (approvalId.Contains('/', StringComparison.Ordinal))
        {
            evidence.ApprovalId.Should().BeNull();
        }

        if (digest == "sha256:zz")
        {
            evidence.ManifestDigest.Should().BeNull();
        }

        if (channel == "beta")
        {
            evidence.Channel.Should().BeNull();
        }
    }

    [Fact]
    public async Task UnpinnedTrustRoot_IsRefused()
    {
        await ExpectRefusalAsync(Approved() with { TrustRoot = "attacker" }, "trust_root_untrusted");
        journal.ReadAll().Single().TrustRoot.Should().Be("untrusted");
    }

    [Fact]
    public async Task ExpiredSigstoreTrustedRoot_IsRefused()
    {
        File.WriteAllText(trustedRootPath, TrustedRoot("2020-01-01T00:00:00Z", "2021-01-01T00:00:00Z"));

        await ExpectRefusalAsync(Approved(), "trust_root_expired");
    }

    [Fact]
    public async Task NotYetValidSigstoreTrustedRoot_IsRefused()
    {
        File.WriteAllText(trustedRootPath, TrustedRoot("2030-01-01T00:00:00Z", null));

        await ExpectRefusalAsync(Approved(), "trust_root_expired");
    }

    [Theory]
    [InlineData("{\"mediaType\":\"application/json\",\"certificateAuthorities\":[{}],\"tlogs\":[{}]}")]
    [InlineData("{\"mediaType\":\"application/vnd.dev.sigstore.trustedroot+json;version=0.1\",\"certificateAuthorities\":[],\"tlogs\":[]}")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task InvalidSigstoreTrustedRoot_IsRefused(string contents)
    {
        File.WriteAllText(trustedRootPath, contents);

        await ExpectRefusalAsync(Approved(), "trust_root_invalid");
    }

    [Fact]
    public async Task MissingOrRelativeTrustedRoot_IsRefused()
    {
        await ExpectRefusalAsync(Approved(), "trust_root_not_configured", Context() with { TrustedRootPath = "" });
        await ExpectRefusalAsync(Approved(), "trust_root_invalid", Context() with { TrustedRootPath = "trusted_root.json" });
        await ExpectRefusalAsync(Approved(), "trust_root_invalid", Context() with { TrustedRootPath = Path.Combine(root.FullName, "absent.json") });
    }

    [Fact]
    public async Task RelativeCosignPath_IsRefused()
    {
        await ExpectRefusalAsync(Approved(), "cosign_not_configured", Context() with { CosignPath = "cosign" });
    }

    [Fact]
    public async Task DowngradeBelowReplayHighWater_IsRefused()
    {
        using FileHostUpdateReplayStore store = NewReplayStore();
        await store.DecideAsync(Candidate(Sequence + 1, "insider:1.2.3-insider.43"), HostUpdateReplayIntent.Admit, default);

        await ExpectRefusalAsync(Approved(), "replay_downgrade", replay: store);
    }

    [Fact]
    public async Task DifferentReleaseAtSameSequence_IsRefused()
    {
        using FileHostUpdateReplayStore store = NewReplayStore();
        await store.DecideAsync(Candidate(Sequence, ReleaseId, digest: "sha256:" + new string('1', 64)), HostUpdateReplayIntent.Admit, default);

        await ExpectRefusalAsync(Approved(), "replay_sequence_conflict", replay: store);
    }

    [Fact]
    public async Task AlreadyAdmittedRelease_IsRefused_AsReplay()
    {
        using FileHostUpdateReplayStore store = NewReplayStore();
        await store.DecideAsync(Candidate(Sequence, ReleaseId), HostUpdateReplayIntent.Admit, default);

        await ExpectRefusalAsync(Approved(), "replay_already_admitted", replay: store);
    }

    [Fact]
    public async Task OfflineImportedRelease_RemainsVerifiable_AndReplayStateIsNotAdvanced()
    {
        using FileHostUpdateReplayStore store = NewReplayStore();
        await store.DecideAsync(Candidate(Sequence, ReleaseId), HostUpdateReplayIntent.Import, default);
        string statePath = Path.Combine(root.FullName, "replay", "host-update-replay.json");
        byte[] before = File.ReadAllBytes(statePath);

        HostUpdateDaemonVerificationResult result = await Verifier(store).VerifyAsync(Approved(), Context(), default);

        result.Verified.Should().BeTrue(result.RefusalCode);
        File.ReadAllBytes(statePath).Should().Equal(before);
    }

    [Fact]
    public async Task FirstVerification_WithEmptyReplayState_DoesNotChangeReplayState()
    {
        using FileHostUpdateReplayStore store = NewReplayStore();
        string statePath = Path.Combine(root.FullName, "replay", "host-update-replay.json");
        byte[] before = File.ReadAllBytes(statePath);

        HostUpdateDaemonVerificationResult result = await Verifier(store).VerifyAsync(Approved(), Context(), default);

        result.Verified.Should().BeTrue(result.RefusalCode);
        File.ReadAllBytes(statePath).Should().Equal(before);
    }

    [Fact]
    public async Task MissingOrRolledBackReplayState_IsRefused_WithoutReset()
    {
        string replayRoot = Path.Combine(root.FullName, "replay");
        Directory.CreateDirectory(replayRoot);
        using FileHostUpdateReplayStore store = new(replayRoot, new InMemoryAnchor(epoch: 3));

        await ExpectRefusalAsync(Approved(), "replay_state_unavailable", replay: store);
        File.Exists(Path.Combine(replayRoot, "host-update-replay.json")).Should().BeFalse();
    }

    [Fact]
    public async Task UnrecordableSuccess_IsRefused()
    {
        var failing = new FailingJournal();

        HostUpdateDaemonVerificationResult result = await new HostUpdateDaemonReleaseVerifier(
            source, new NoReplay(), failing, Factory, time).VerifyAsync(Approved(), Context(), default);

        result.Verified.Should().BeFalse();
        result.RefusalCode.Should().Be("verification_evidence_unavailable");
    }

    [Fact]
    public async Task UnrecordableRefusal_KeepsOriginalRefusalCode()
    {
        HostUpdateDaemonVerificationResult result = await new HostUpdateDaemonReleaseVerifier(
            source, new NoReplay(), new FailingJournal(), Factory, time).VerifyAsync(Approved() with { ExpiresAt = now }, Context(), default);

        result.RefusalCode.Should().Be("approval_expired");
    }

    [Fact]
    public async Task TamperedEvidenceJournal_FailsClosed()
    {
        await Verifier().VerifyAsync(Approved() with { ExpiresAt = now }, Context(), default);
        string path = Path.Combine(root.FullName, "state", FileHostUpdateDaemonVerificationJournal.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("approval_expired", "verified", StringComparison.Ordinal));

        journal.Invoking(j => j.ReadAll()).Should().Throw<InvalidDataException>().WithMessage("journal_verification_integrity_failure");
        HostUpdateDaemonVerificationResult result = await Verifier().VerifyAsync(Approved(), Context(), default);
        result.RefusalCode.Should().Be("verification_evidence_unavailable");
    }

    [Fact]
    public async Task RepeatedIdenticalRefusal_IsRecordedOnce()
    {
        HostUpdateDaemonApprovedRelease expired = Approved() with { ExpiresAt = now };

        await Verifier().VerifyAsync(expired, Context(), default);
        await Verifier().VerifyAsync(expired, Context(), default);
        await Verifier().VerifyAsync(Approved(), Context(), default);

        journal.ReadAll().Select(e => e.Code).Should().Equal("approval_expired", "verified");
    }

    [Fact]
    public async Task RepeatedRefusal_ForADifferentRelease_IsRecordedSeparately()
    {
        await Verifier().VerifyAsync(Approved() with { ExpiresAt = now }, Context(), default);
        await Verifier().VerifyAsync(Approved() with { ExpiresAt = now, Sequence = Approved().Sequence + 1 }, Context(), default);

        journal.ReadAll().Select(e => e.Sequence).Should().Equal(Approved().Sequence, Approved().Sequence + 1);
    }

    [Fact]
    public void EvidenceJournal_IsBounded_AndFailsClosedWhenFull()
    {
        string path = Path.Combine(root.FullName, "bounded", FileHostUpdateDaemonVerificationJournal.FileName);
        var bounded = new FileHostUpdateDaemonVerificationJournal(path);
        HostUpdateDaemonVerificationEvidence Evidence(int i) => new(
            "e" + i, null, null, null, i + 1, null, null, HostUpdateTrustRoot.DefaultTrustRoot, HostUpdateTrustRoot.Fingerprint,
            null, HostUpdateDaemonVerificationEvidence.RefusedOutcome, "approval_expired", now);
        var lines = new List<string>();
        string previous = string.Empty;
        for (int i = 0; i < FileHostUpdateDaemonVerificationJournal.MaximumRecords; i++)
        {
            string payload = System.Text.Json.JsonSerializer.Serialize(Evidence(i));
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(previous + payload)));
            lines.Add(System.Text.Json.JsonSerializer.Serialize(new { PreviousHash = previous, Payload = payload, Hash = hash }));
            previous = hash;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");

        bounded.ReadAll().Should().HaveCount(FileHostUpdateDaemonVerificationJournal.MaximumRecords);
        bounded.Invoking(j => j.Append(Evidence(-1))).Should().Throw<InvalidDataException>().WithMessage("journal_verification_full");
    }

    [Fact]
    public async Task EvidenceJournal_ConcurrentReadersAndWriters_NeverObserveATornFile()
    {
        HostUpdateDaemonVerificationEvidence Evidence(int i) => new(
            "c" + i, null, null, null, i + 1, null, null, HostUpdateTrustRoot.DefaultTrustRoot, HostUpdateTrustRoot.Fingerprint,
            null, HostUpdateDaemonVerificationEvidence.RefusedOutcome, "approval_expired", now);
        string path = Path.Combine(root.FullName, "concurrent", FileHostUpdateDaemonVerificationJournal.FileName);
        var writer = new FileHostUpdateDaemonVerificationJournal(path);
        var reader = new FileHostUpdateDaemonVerificationJournal(path);

        Task write = Task.Run(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                writer.Append(Evidence(i));
            }
        });
        Task read = Task.Run(async () =>
        {
            while (!write.IsCompleted)
            {
                _ = reader.ReadAll();
                await Task.Yield();
            }
        });

        await Task.WhenAll(write, read);
        reader.ReadAll().Should().HaveCount(40);
    }

    [Fact]
    public void EvidenceJournal_ReadDoesNotDeleteAStagedAppend()
    {
        string path = Path.Combine(root.FullName, "staged", FileHostUpdateDaemonVerificationJournal.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".staged", "in-flight");
        var staged = new FileHostUpdateDaemonVerificationJournal(path);

        staged.ReadAll().Should().BeEmpty();
        File.Exists(path + ".staged").Should().BeTrue();
    }

    [Fact]
    public async Task JournalReader_ReportsLatestVerificationCode()
    {
        string state = Path.Combine(root.FullName, "state");
        var reader = new HostUpdateDaemonJournalReader(
            state,
            new FileHostUpdateExecutionLock(Path.Combine(state, FileHostUpdateExecutionLock.FileName)),
            new FileHostUpdateExecutionJournal(Path.Combine(state, "journal.ndjson")),
            journal);
        Directory.CreateDirectory(state);

        reader.Read().VerificationCode.Should().Be(HostUpdateDaemonJournalSnapshot.NoVerificationCode);
        await Verifier().VerifyAsync(Approved() with { ExpiresAt = now }, Context(), default);
        reader.Read().VerificationCode.Should().Be("refused:approval_expired");

        string path = Path.Combine(state, FileHostUpdateDaemonVerificationJournal.FileName);
        File.AppendAllText(path, "{\"PreviousHash\":\"x\",\"Payload\":\"{}\",\"Hash\":\"00\"}\n");
        HostUpdateDaemonJournalSnapshot tampered = reader.Read();
        tampered.Failed.Should().BeTrue();
        tampered.VerificationCode.Should().Be("journal_verification_integrity_failure");
    }

    [Fact]
    public async Task GitHubSource_FetchesExactChannelTagAssets()
    {
        var handler = new GitHubHandler(ReleaseJson(Tag, prerelease: true), manifest);
        var discovery = new GitHubSignedReleaseDiscovery(new HttpClient(handler), new RecordingVerifier());

        HostUpdateDaemonSignedArtifacts? artifacts = await discovery.FetchAsync("insider", Tag, default);

        artifacts.Should().NotBeNull();
        artifacts!.Manifest.Should().Equal(manifest);
        artifacts.Bundle.Should().Equal("bundle"u8.ToArray());
        handler.Paths.Should().Equal(
            "/repos/OlyForge3D/PrintFarmer/releases/tags/" + Tag,
            "/repos/OlyForge3D/PrintFarmer/releases/assets/11",
            "/repos/OlyForge3D/PrintFarmer/releases/assets/12");
    }

    [Theory]
    [InlineData("stable", Tag)]
    [InlineData("insider", "v1.2.3")]
    [InlineData("insider", "v1.2.3-insider.42/../../x")]
    [InlineData("beta", Tag)]
    public async Task GitHubSource_RefusesTagOutsideChannel_WithoutRequest(string channel, string tag)
    {
        var handler = new GitHubHandler(ReleaseJson(Tag, prerelease: true), manifest);

        HostUpdateDaemonSignedArtifacts? artifacts = await new GitHubSignedReleaseDiscovery(new HttpClient(handler), new RecordingVerifier())
            .FetchAsync(channel, tag, default);

        artifacts.Should().BeNull();
        handler.Paths.Should().BeEmpty();
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("not-prerelease")]
    [InlineData("tag-mismatch")]
    [InlineData("duplicate-manifest")]
    [InlineData("missing-bundle")]
    [InlineData("not-found")]
    public async Task GitHubSource_RefusesUnexpectedRelease(string variant)
    {
        string json = variant switch
        {
            "draft" => ReleaseJson(Tag, prerelease: true, draft: true),
            "not-prerelease" => ReleaseJson(Tag, prerelease: false),
            "tag-mismatch" => ReleaseJson("v1.2.3-insider.41", prerelease: true),
            "duplicate-manifest" => ReleaseJson(Tag, prerelease: true, extraAsset: "update-manifest.json"),
            "missing-bundle" => ReleaseJson(Tag, prerelease: true, bundle: false),
            _ => string.Empty,
        };
        var handler = new GitHubHandler(json, manifest) { NotFound = variant == "not-found" };

        HostUpdateDaemonSignedArtifacts? artifacts = await new GitHubSignedReleaseDiscovery(new HttpClient(handler), new RecordingVerifier())
            .FetchAsync("insider", Tag, default);

        artifacts.Should().BeNull();
        handler.Paths.Should().ContainSingle("no asset is downloaded for a refused release");
    }

    private async Task ExpectRefusalAsync(
        HostUpdateDaemonApprovedRelease approved,
        string code,
        HostUpdateDaemonVerificationContext? context = null,
        IHostUpdateReplayAdmissionReader? replay = null)
    {
        HostUpdateDaemonVerificationResult result = await Verifier(replay).VerifyAsync(approved, context ?? Context(), default);

        result.Verified.Should().BeFalse();
        result.Release.Should().BeNull();
        result.RefusalCode.Should().Be(code);
        journal.ReadAll()[^1].Code.Should().Be(code);
        journal.ReadAll()[^1].Outcome.Should().Be(HostUpdateDaemonVerificationEvidence.RefusedOutcome);
    }

    private HostUpdateDaemonReleaseVerifier Verifier(IHostUpdateReplayAdmissionReader? replay = null) =>
        new(source, replay ?? new NoReplay(), journal, Factory, time);

    private RecordingVerifier Factory(CosignVerifierOptions options)
    {
        signature.Options.Add(options);
        return signature;
    }

    private HostUpdateDaemonApprovedRelease Approved() =>
        new("approval-1", ReleaseId, "insider", Sequence, manifestDigest, HostUpdateTrustRoot.DefaultTrustRoot, now.AddMinutes(4));

    private HostUpdateDaemonVerificationContext Context() => new("insider", "linux-amd64", trustedRootPath, cosignPath);

    private FileHostUpdateReplayStore NewReplayStore()
    {
        string replayRoot = Path.Combine(root.FullName, "replay");
        Directory.CreateDirectory(replayRoot);
        string checksum = HostUpdateCanonical.Hash(new { Version = 1, Epoch = 0L, HighWater = Array.Empty<object>(), Identities = Array.Empty<object>() });
        string json = JsonSerializer.Serialize(new
        {
            Version = 1,
            Epoch = 0L,
            Checksum = checksum,
            HighWaterByNamespace = new Dictionary<string, object>(),
            Identities = new Dictionary<string, object>(),
        });
        File.WriteAllText(Path.Combine(replayRoot, "host-update-replay.json"), json);
        return new FileHostUpdateReplayStore(replayRoot, new InMemoryAnchor(hash: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))));
    }

    private VerifiedHostUpdateCandidate Candidate(long sequence, string releaseId, string? digest = null) =>
        new(releaseId, SourceCommit, sequence, digest ?? manifestDigest, "insider", true, true, true, true, true, true,
            new(Amd64Digests[0], Amd64Digests[1], Amd64Digests[2], Amd64Digests[3], Amd64Digests[4], Amd64Digests[5]),
            TrustRoot: HostUpdateTrustRoot.DefaultTrustRoot);

    private static HostUpdateExecutionRequest RequestFor(HostUpdateDaemonVerifiedRelease release) =>
        new(release.ReleaseId, release.Sequence, release.ManifestDigest, release.SourceCommit, HostUpdateExecutionChannel.Insider, [.. release.Targets.Reverse()])
        {
            TrustRoot = HostUpdateTrustRoot.DefaultTrustRoot,
            HostPlatform = release.HostPlatform,
        };

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string TrustedRoot(string start, string? end)
    {
        object validFor = end is null ? new { start } : new { start, end };
        return JsonSerializer.Serialize(new
        {
            mediaType = "application/vnd.dev.sigstore.trustedroot+json;version=0.1",
            certificateAuthorities = new[] { new { validFor } },
            tlogs = new[] { new { publicKey = new { validFor } } },
        });
    }

    private static string ReleaseJson(string tag, bool prerelease, bool draft = false, bool bundle = true, string? extraAsset = null)
    {
        var assets = new List<object> { new { id = 11L, name = "update-manifest.json" } };
        if (bundle)
        {
            assets.Add(new { id = 12L, name = "update-manifest.sigstore.json" });
        }

        if (extraAsset is not null)
        {
            assets.Add(new { id = 13L, name = extraAsset });
        }

        return JsonSerializer.Serialize(new { id = 1L, tag_name = tag, draft, prerelease, assets });
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VERSION")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    private sealed class FixedTime(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FakeSource(HostUpdateDaemonSignedArtifacts? artifacts) : IHostUpdateDaemonReleaseSource
    {
        public HostUpdateDaemonSignedArtifacts? Artifacts { get; set; } = artifacts;

        public Exception? Throw { get; set; }

        public List<(string Channel, string Tag)> Requests { get; } = [];

        public Task<HostUpdateDaemonSignedArtifacts?> FetchAsync(string channel, string tag, CancellationToken cancellationToken)
        {
            Requests.Add((channel, tag));
            return Throw is null ? Task.FromResult(Artifacts) : Task.FromException<HostUpdateDaemonSignedArtifacts?>(Throw);
        }
    }

    private sealed class RecordingVerifier : ISignedReleaseVerifier
    {
        public bool Result { get; set; } = true;

        public Exception? Throw { get; set; }

        public List<CosignVerifierOptions> Options { get; } = [];

        public List<string> Identities { get; } = [];

        public List<byte[]> RootCopies { get; } = [];

        public Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
        {
            Identities.Add(certificateIdentity);
            if (Options.Count > 0 && Options[^1].TrustedRootPath is { } copy && File.Exists(copy))
            {
                RootCopies.Add(File.ReadAllBytes(copy));
            }

            return Throw is null ? Task.FromResult(Result) : Task.FromException<bool>(Throw);
        }
    }

    private sealed class NoReplay : IHostUpdateReplayAdmissionReader
    {
        public Task<string?> EvaluateAdmissionAsync(VerifiedHostUpdateCandidate candidate, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class FailingJournal : IHostUpdateDaemonVerificationJournal
    {
        public void Append(HostUpdateDaemonVerificationEvidence evidence) => throw new IOException("disk full");

        public IReadOnlyList<HostUpdateDaemonVerificationEvidence> ReadAll() => [];
    }

    private sealed class InMemoryAnchor(long epoch = 0, string hash = "") : IHostUpdateReplayAnchor
    {
        private long currentEpoch = epoch;
        private string stateHash = hash;

        public Task<long> ReadEpochAsync(CancellationToken ct) => Task.FromResult(currentEpoch);

        public Task<string> ReadStateHashAsync(CancellationToken ct) => Task.FromResult(stateHash);

        public Task AdvanceEpochAsync(long epoch, string stateHash, CancellationToken ct)
        {
            currentEpoch = epoch;
            this.stateHash = stateHash;
            return Task.CompletedTask;
        }
    }

    private sealed class GitHubHandler(string releaseJson, byte[] manifest) : HttpMessageHandler
    {
        public bool NotFound { get; init; }

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                return Task.FromResult(NotFound
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson) });
            }

            byte[] content = path.EndsWith("/11", StringComparison.Ordinal) ? manifest : "bundle"u8.ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
