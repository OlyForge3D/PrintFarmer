using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// Issue #3064: <c>offline-admit</c> records a verified offline bundle in the durable replay store
/// and refuses replays, downgrades and cross-channel imports without an API process.
/// </summary>
public sealed class HostUpdateCliOfflineAdmitTests : IDisposable, IAsyncLifetime
{
    private readonly CliHostFixture _host = new();
    private readonly string _staging;
    private readonly byte[] _manifest;
    private readonly byte[] _signature = "{\"mediaType\":\"application/vnd.dev.sigstore.bundle.v0.3+json\"}"u8.ToArray();
    private readonly string _trustedRoot;
    private readonly Func<CosignVerifierOptions, ISignedReleaseVerifier> _originalVerifierFactory = HostUpdateOfflineAdmission.VerifierFactory;
    private readonly FakeVerifier _verifier = new();

    public HostUpdateCliOfflineAdmitTests()
    {
        _manifest = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "scripts", "ci", "fixtures", "update-manifest.golden.json"));
        _staging = Path.Combine(_host.Root, "staging");
        Directory.CreateDirectory(_staging);
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineAdmission.ManifestName), _manifest);
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineAdmission.SignatureName), _signature);
        _trustedRoot = Path.Combine(_host.Root, "trusted_root.json");
        File.WriteAllText(_trustedRoot, "{}");
        WriteRecord(Digest(_manifest));
        HostUpdateOfflineAdmission.VerifierFactory = options =>
        {
            _verifier.Options.Add(options);
            return _verifier;
        };
    }

    public void Dispose()
    {
        HostUpdateOfflineAdmission.VerifierFactory = _originalVerifierFactory;
        _host.Dispose();
    }

    public async Task InitializeAsync()
    {
        await _host.ProvisionPolicyAsync();
        if (CliHostFixture.HostStateSupported)
        {
            await _host.ChangePolicyAsync("insider");
            using var anchor = new FileHostUpdateReplayAnchor(Paths());
            await anchor.ProvisionAsync(CancellationToken.None);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(new[] { "offline-admit", "--channel", "insider" }, "missing_option:--staging")]
    [InlineData(new[] { "offline-admit", "--staging", "relative/dir", "--channel", "stable" }, "staging_not_absolute")]
    [InlineData(new[] { "offline-admit", "--release", "stable:1.2.3" }, "unknown_option:--release")]
    public async Task Invalid_arguments_are_usage_errors(string[] args, string expected)
    {
        CliRun run = await RunAsync(args);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expected);
    }

    [Fact]
    public async Task Missing_or_unknown_channel_is_a_usage_error()
    {
        (await RunAsync(["offline-admit", "--staging", _staging])).Error.Should().Contain("missing_option:--channel");
        (await RunAsync(["offline-admit", "--staging", _staging, "--channel", "beta"])).Error.Should().Contain("invalid_channel");
    }

    [Fact]
    public async Task Missing_or_relative_trusted_root_or_relative_cosign_is_a_usage_error()
    {
        (await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider"]))
            .Error.Should().Contain("missing_option:--trusted-root");
        (await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider", "--trusted-root", "root.json"]))
            .Error.Should().Contain("trusted_root_not_absolute");
        (await RunAsync([.. Admit("insider"), "--cosign", "cosign"])).Error.Should().Contain("cosign_not_absolute");
    }

    [HostStateFact]
    public async Task Staged_manifest_is_reverified_offline_against_the_trusted_root_and_channel_identity()
    {
        string cosign = Path.Combine(_host.Root, "bin", "cosign");

        JsonElement admitted = Envelope(await RunAsync([.. Admit("insider"), "--cosign", cosign]));

        admitted.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, admitted.ToString());
        CosignVerifierOptions options = _verifier.Options.Should().ContainSingle().Subject;
        options.ExecutablePath.Should().Be(cosign);
        options.TrustedRootPath.Should().Be(HostUpdateOfflineAdmission.ResolvePhysicalPath(_trustedRoot));
        FakeVerifier.Call call = _verifier.Calls.Should().ContainSingle().Subject;
        call.Manifest.Should().Equal(_manifest);
        call.Bundle.Should().Equal(_signature);
        call.Identity.Should().Be(HostUpdateTrustRoot.InsiderCertificateIdentity);
    }

    [HostStateFact]
    public async Task Forged_staging_whose_signature_does_not_verify_is_refused_before_the_replay_store()
    {
        _verifier.Result = false;

        JsonElement refused = Envelope(await RunAsync(Admit("insider")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_signature_unverified");
        _verifier.Result = true;
        JsonElement admitted = Envelope(await RunAsync(Admit("insider")));
        admitted.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeFalse("the forged attempt recorded nothing");
    }

    [HostStateFact]
    public async Task Trusted_root_inside_the_mutable_staging_directory_is_refused()
    {
        string inside = Path.Combine(_staging, "trusted_root.json");
        File.WriteAllText(inside, "{}");

        JsonElement refused = Envelope(await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider", "--trusted-root", inside, "--json"]));

        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("trusted_root_inside_staging");
        _verifier.Calls.Should().BeEmpty();
    }

    [HostStateTheory]
    [InlineData(LinkKind.Symlink)]
    [InlineData(LinkKind.Junction)]
    public async Task Trusted_root_reached_through_a_linked_parent_that_aliases_staging_is_refused(LinkKind kind)
    {
        File.WriteAllText(Path.Combine(_staging, "trusted_root.json"), "{}");
        string alias = Path.Combine(_host.Root, "root-alias");
        if (!TryCreateDirectoryLink(alias, _staging, kind))
        {
            return;
        }

        JsonElement refused = Envelope(await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider",
            "--trusted-root", Path.Combine(alias, "trusted_root.json"), "--json"]));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("trusted_root_inside_staging");
        _verifier.Calls.Should().BeEmpty("the aliased trust material is refused before Cosign runs");
        JsonElement admitted = Envelope(await RunAsync(Admit("insider")));
        admitted.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeFalse("the refusal touched no replay state");
    }

    [HostStateTheory]
    [InlineData(LinkKind.Symlink)]
    [InlineData(LinkKind.Junction)]
    public async Task External_trusted_root_reached_through_a_linked_parent_is_verified_at_its_physical_path(LinkKind kind)
    {
        string external = Path.Combine(_host.Root, "external-trust");
        Directory.CreateDirectory(external);
        string physical = Path.Combine(external, "trusted_root.json");
        File.WriteAllText(physical, "{}");
        string alias = Path.Combine(_host.Root, "trust-alias");
        if (!TryCreateDirectoryLink(alias, external, kind))
        {
            return;
        }

        JsonElement admitted = Envelope(await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider",
            "--trusted-root", Path.Combine(alias, "trusted_root.json"), "--json"]));

        admitted.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, admitted.ToString());
        _verifier.Options.Should().ContainSingle().Which.TrustedRootPath
            .Should().Be(HostUpdateOfflineAdmission.ResolvePhysicalPath(physical));
    }

    public enum LinkKind
    {
        Symlink,
        Junction,
    }

    // Symbolic links need privilege on Windows and junctions exist only there, so an unsupported
    // combination returns false and the case is exercised on the platform that supports it.
    private static bool TryCreateDirectoryLink(string link, string target, LinkKind kind)
    {
        if (kind == LinkKind.Junction)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            process.ExitCode.Should().Be(0, "junctions need no privilege on Windows");
            return true;
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [HostStateFact]
    public async Task Missing_signature_bundle_is_refused()
    {
        File.Delete(Path.Combine(_staging, HostUpdateOfflineAdmission.SignatureName));

        JsonElement refused = Envelope(await RunAsync(Admit("insider")));

        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_missing:" + HostUpdateOfflineAdmission.SignatureName);
    }

    [HostStateFact]
    public async Task Fresh_release_is_imported_and_a_second_import_reuses_the_decision()
    {
        JsonElement first = Envelope(await RunAsync(Admit("insider")));
        JsonElement second = Envelope(await RunAsync(Admit("insider")));

        first.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, first.ToString());
        first.GetProperty("result").GetProperty("disposition").GetString().Should().Be("Imported");
        first.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeFalse();
        second.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success);
        second.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeTrue();
        second.GetProperty("result").GetProperty("correlationId").GetString().Should().Be(first.GetProperty("result").GetProperty("correlationId").GetString());
    }

    [HostStateFact]
    public async Task Release_below_the_durable_high_water_is_refused()
    {
        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(_manifest));
        using (var anchor = new FileHostUpdateReplayAnchor(Paths()))
        using (var store = new FileHostUpdateReplayStore(Paths().Root, anchor))
        {
            HostUpdateReplayDecision newer = await store.DecideAsync(Candidate(manifest, manifest.Sequence + 1), HostUpdateReplayIntent.Admit, CancellationToken.None);
            newer.Disposition.Should().Be(HostUpdateReplayDisposition.Accepted);
        }

        JsonElement refused = Envelope(await RunAsync(Admit("insider")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("reason").GetString().Should().Be("replay_rejected");
    }

    [HostStateFact]
    public async Task Channel_other_than_the_standing_policy_is_refused()
    {
        JsonElement refused = Envelope(await RunAsync(Admit("stable")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("channel_mismatch_policy");
    }

    [HostStateFact]
    public async Task Manifest_channel_other_than_the_requested_channel_is_refused()
    {
        await _host.ChangePolicyAsync("stable");

        JsonElement refused = Envelope(await RunAsync(Admit("stable")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("channel_mismatch_manifest");
    }

    [HostStateFact]
    public async Task Verification_record_not_bound_to_the_staged_manifest_is_refused()
    {
        WriteRecord("sha256:" + new string('0', 64));

        JsonElement refused = Envelope(await RunAsync(Admit("insider")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_verification_mismatch");
    }

    [HostStateFact]
    public async Task Missing_verification_record_is_refused()
    {
        File.Delete(Path.Combine(_staging, HostUpdateOfflineAdmission.VerificationName));

        JsonElement refused = Envelope(await RunAsync(Admit("insider")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_missing:" + HostUpdateOfflineAdmission.VerificationName);
    }

    [HostStateFact]
    public async Task Held_execution_lock_is_reported_without_admitting()
    {
        using IHostUpdateExecutionLease? held = FileHostUpdateExecutionLock.TryAcquireExisting(_host.LockPath);
        held.Should().NotBeNull();

        CliRun run = await RunAsync(Admit("insider"));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.LockHeld);
    }

    [Fact]
    public async Task Disabled_host_state_is_configuration_unproven()
    {
        CliRun run = await RunAsync(Admit("insider"), _host.Configuration(v => v["HostUpdates:HostState:Enabled"] = "false"));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("host_state_not_enabled");
    }

    [HostStateFact]
    public async Task Manifest_from_a_forged_source_branch_is_refused_before_the_replay_store()
    {
        StageManifest(json => json.Replace("\"sourceBranch\":\"development\"", "\"sourceBranch\":\"feature/forged\"", StringComparison.Ordinal));

        JsonElement refused = Envelope(await RunAsync(Admit("insider")));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_manifest_invalid");
        _verifier.Calls.Should().BeEmpty();
        StageManifest(json => json);
        JsonElement genuine = Envelope(await RunAsync(Admit("insider")));
        genuine.GetProperty("result").GetProperty("disposition").GetString().Should().Be("Imported");
        genuine.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeFalse();
    }

    [HostStateFact]
    public async Task Moved_release_alias_at_the_imported_sequence_is_refused_and_the_original_import_survives()
    {
        JsonElement first = Envelope(await RunAsync(Admit("insider")));

        StageManifest(json => json.Replace("\"sourceCommit\":\"" + new string('a', 40) + "\"", "\"sourceCommit\":\"" + new string('b', 40) + "\"", StringComparison.Ordinal));
        JsonElement moved = Envelope(await RunAsync(Admit("insider")));
        StageManifest(json => json);
        JsonElement original = Envelope(await RunAsync(Admit("insider")));

        moved.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, moved.ToString());
        moved.GetProperty("result").GetProperty("reason").GetString().Should().Be("replay_rejected");
        original.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success);
        original.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeTrue();
        original.GetProperty("result").GetProperty("correlationId").GetString().Should().Be(first.GetProperty("result").GetProperty("correlationId").GetString());
    }

    [HostStateFact]
    public async Task Evidence_staged_under_the_prior_policy_is_refused_after_a_policy_edit_and_reused_after_the_round_trip()
    {
        JsonElement imported = Envelope(await RunAsync(Admit("insider")));
        await _host.ChangePolicyAsync("stable");

        JsonElement late = Envelope(await RunAsync(Admit("insider")));
        await _host.ChangePolicyAsync("insider");
        JsonElement back = Envelope(await RunAsync(Admit("insider")));

        late.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        late.GetProperty("result").GetProperty("code").GetString().Should().Be("channel_mismatch_policy");
        back.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success);
        back.GetProperty("result").GetProperty("reused").GetBoolean().Should().BeTrue();
        back.GetProperty("result").GetProperty("correlationId").GetString().Should().Be(imported.GetProperty("result").GetProperty("correlationId").GetString());
    }

    [HostStateFact]
    public async Task Restored_older_replay_state_fails_closed_instead_of_readmitting()
    {
        string statePath = Path.Combine(Paths().Root, "host-update-replay.json");
        byte[] beforeImport = await File.ReadAllBytesAsync(statePath);
        Envelope(await RunAsync(Admit("insider"))).GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success);

        await File.WriteAllBytesAsync(statePath, beforeImport);
        CliRun run = await RunAsync(Admit("insider"));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable, run.Output);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("host_update_replay_state_rollback");
        (await File.ReadAllBytesAsync(statePath)).Should().Equal(beforeImport);
    }

    [Theory]
    [InlineData("host_update_replay_state_rollback", "host_update_replay_state_rollback")]
    [InlineData("host_update_replay_anchor_rollback", "host_update_replay_anchor_rollback")]
    [InlineData("journal_corrupt", "journal_corrupt")]
    [InlineData("host_update_replay_state_rollback at C:\\state", "state_unreadable:InvalidDataException")]
    [InlineData("host_update_policy_fence_invalid", "state_unreadable:InvalidDataException")]
    [InlineData("Journal record is invalid.", "state_unreadable:InvalidDataException")]
    [InlineData("host_update_replay_state_rollback\n", "state_unreadable:InvalidDataException")]
    [InlineData("journal_corrupt\n", "state_unreadable:InvalidDataException")]
    public void State_failure_code_passes_only_fixed_journal_and_replay_codes(string message, string expected)
    {
        HostUpdateCli.StateFailureCode(new InvalidDataException(message)).Should().Be(expected);
    }

    private string[] Admit(string channel) => ["offline-admit", "--staging", _staging, "--channel", channel, "--trusted-root", _trustedRoot, "--json"];

    private void StageManifest(Func<string, string> edit)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(edit(System.Text.Encoding.UTF8.GetString(_manifest)));
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineAdmission.ManifestName), bytes);
        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        WriteRecord(Digest(bytes), manifest.Channel, manifest.Version);
    }

    private void WriteRecord(string manifestDigest)
    {
        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(System.Text.Encoding.UTF8.GetString(_manifest));
        WriteRecord(manifestDigest, manifest.Channel, manifest.Version);
    }

    private void WriteRecord(string manifestDigest, string channel, string version)
    {
        string json = JsonSerializer.Serialize(new
        {
            schema = 1,
            decision = HostUpdateOfflineAdmission.VerifiedDecision,
            release = new { channel, version },
            manifestDigest,
        });
        File.WriteAllText(Path.Combine(_staging, HostUpdateOfflineAdmission.VerificationName), json);
    }

    private HostStatePath Paths() =>
        new(Options.Create(new HostStateOptions { Enabled = true, RootPath = _host.HostStateRoot, WindowsSecurityAttested = true }));

    private static VerifiedHostUpdateCandidate Candidate(SignedUpdateManifest manifest, long sequence) =>
        new($"{manifest.Channel}:9.9.9", manifest.SourceCommit, sequence, "sha256:" + new string('9', 64), manifest.Channel,
            true, true, true, true, true, true, new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "scripts", "ci", "fixtures")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private Task<CliRun> RunAsync(string[] args) => RunAsync(args, _host.Configuration());

    private static async Task<CliRun> RunAsync(string[] args, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(args, configuration, output, error, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    private static JsonElement Envelope(CliRun run)
    {
        using JsonDocument document = JsonDocument.Parse(run.Output);
        return document.RootElement.Clone();
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    private sealed class FakeVerifier : ISignedReleaseVerifier
    {
        public bool Result { get; set; } = true;

        public List<CosignVerifierOptions> Options { get; } = [];

        public List<Call> Calls { get; } = [];

        public Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(manifest.ToArray(), bundle.ToArray(), certificateIdentity));
            return Task.FromResult(Result);
        }

        public sealed record Call(byte[] Manifest, byte[] Bundle, string Identity);
    }
}
