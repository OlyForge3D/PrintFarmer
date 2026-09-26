using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli.Tests;

[Collection("HostUpdateOfflineVerifier")]
public sealed partial class HostUpdateCliOfflineActivateTests : IDisposable, IAsyncLifetime
{
    private readonly CliHostFixture _host = new();
    private readonly string _staging;
    private readonly byte[] _manifest;
    private readonly byte[] _signature = "{\"mediaType\":\"application/vnd.dev.sigstore.bundle.v0.3+json\"}"u8.ToArray();
    private readonly string _trustedRoot;
    private readonly Func<CosignVerifierOptions, ISignedReleaseVerifier> _originalVerifierFactory = HostUpdateOfflineAdmission.VerifierFactory;
    private readonly Func<string> _originalHostPlatformFactory = HostUpdateOfflineActivation.HostPlatformFactory;
    private readonly FakeVerifier _verifier = new();
    private readonly FakeLocalImageVerifier _imageVerifier = new();
    private readonly FakeSafetyProbe _safetyProbe = new();

    public HostUpdateCliOfflineActivateTests()
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
        HostUpdateOfflineActivation.HostPlatformFactory = () => "linux-amd64";
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

    public void Dispose()
    {
        HostUpdateOfflineAdmission.VerifierFactory = _originalVerifierFactory;
        HostUpdateOfflineActivation.HostPlatformFactory = _originalHostPlatformFactory;
        _host.Dispose();
    }

    [Theory]
    [InlineData(new[] { "offline-activate", "--channel", "insider" }, "missing_option:--staging")]
    [InlineData(new[] { "offline-activate", "--staging", "relative", "--channel", "insider" }, "staging_not_absolute")]
    [InlineData(new[] { "offline-activate", "--staging", "{abs}", "--channel", "beta" }, "invalid_channel")]
    public async Task Invalid_arguments_are_usage_errors(string[] args, string expected)
    {
        string absolutePath = Path.GetFullPath(Path.GetTempPath());
        CliRun run = await RunAsync(args.Select(arg => arg == "{abs}" ? absolutePath : arg).ToArray());

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expected);
    }

    [HostStateFact]
    public async Task Imported_bundle_activates_through_executor_with_preloaded_images()
    {
        await ImportAsync();

        JsonElement activated = Envelope(await RunAsync(Activate(), services =>
        {
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.AddSingleton<IHostUpdateExecutor>(sp => new CompletingExecutor(sp.GetRequiredService<IInstalledHostStateStore>()));
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        _imageVerifier.Requests.Should().ContainSingle()
            .Which.ImageSourceMode.Should().Be(HostUpdateImageSourceMode.PreloadedLocal);
        _imageVerifier.Requests[0].Targets.Should().HaveCount(6);
        InstalledHostState? state = await new FileInstalledHostStateStore(Path.Combine(_host.StateDirectory, "installed-state.json"))
            .ReadAsync(CancellationToken.None);
        state!.ReleaseId.Should().Be("insider:1.2.3-insider.42");
    }

    [HostStateFact]
    public async Task Imported_bundle_reaches_real_scoped_executor_with_durable_policy_repository()
    {
        await ImportAsync();
        RecordingExecutionSteps? steps = null;

        JsonElement activated = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(sp =>
            {
                steps = new RecordingExecutionSteps(sp.GetRequiredService<IInstalledHostStateStore>());
                return steps;
            });
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        steps.Should().NotBeNull();
        steps!.Calls.Should().Equal("preflight", "drain", "fence", "backup", "migration", "apply", "verify");
        HostUpdateReplayDecision replay = await HostUpdateOfflineActivation.ReadReplayDecisionAsync(
            _host.Configuration(),
            CurrentCandidate(),
            CancellationToken.None);
        replay.Disposition.Should().Be(HostUpdateReplayDisposition.Accepted);
    }

    [HostStateFact]
    public async Task Integrated_activation_uses_real_executor_steps_without_pull_build_or_remote_health()
    {
        await ImportAsync();
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        var http = new RecordingHealthHttpClientFactory(_host.Configuration()["HostUpdateExecution:HealthCheckBaseUrl"] ?? "http://localhost:5245", succeeds: true);

        JsonElement activated = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
        {
            UseIntegratedBoundaries(services, runner, http);
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        InstalledHostState? state = await new FileInstalledHostStateStore(Path.Combine(_host.StateDirectory, "installed-state.json"))
            .ReadAsync(CancellationToken.None);
        state!.ReleaseId.Should().Be("insider:1.2.3-insider.42");
        runner.ContainsDockerCommand("image", "pull").Should().BeFalse();
        runner.ContainsDockerCommand("build").Should().BeFalse();
        runner.ContainsDockerCommand("buildx").Should().BeFalse();
        runner.ComposeUpCalls.Should().ContainSingle(call =>
            call.Arguments.Contains("--no-build") &&
            PullNever(call.Arguments));
        runner.MigrationRunCalls.Should().NotBeEmpty();
        runner.MigrationRunCalls.Should().OnlyContain(call => PullNever(call.Arguments));
        http.Requests.Should().NotBeEmpty();
        http.Requests.Should().OnlyContain(uri => string.Equals(uri.Host, "localhost", StringComparison.Ordinal));
    }

    [HostStateFact]
    public async Task Integrated_activation_failed_verify_enters_recovery_required_without_pull_and_preserves_prior_state()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request with { ReleaseId = "stable:1.2.2", ManifestDigest = "sha256:" + new string('9', 64) });
        string before = File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json"));
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        var http = new RecordingHealthHttpClientFactory(_host.Configuration()["HostUpdateExecution:HealthCheckBaseUrl"] ?? "http://localhost:5245", succeeds: false);

        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
        {
            UseIntegratedBoundaries(services, runner, http);
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("state").GetString().Should().Be(nameof(HostUpdateExecutionState.RecoveryRequired));
        File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json")).Should().Be(before);
        runner.ContainsDockerCommand("image", "pull").Should().BeFalse();
        runner.ContainsDockerCommand("build").Should().BeFalse();
    }

    [HostStateFact]
    public async Task Activation_refuses_without_imported_replay_evidence_and_preserves_prior_install()
    {
        _host.SeedInstalledState();
        string before = File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json"));
        IReadOnlyDictionary<string, string> snapshot = ReplaySnapshot();

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_rejected");
        File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json")).Should().Be(before);
        ReplaySnapshot().Should().Equal(snapshot);
    }

    [HostStateFact]
    public async Task Activation_refuses_accepted_replay_evidence_and_preserves_replay_bytes()
    {
        await ImportAsync();
        await HostUpdateOfflineActivation.MarkActivatedAsync(_host.Configuration(), CurrentCandidate(), CancellationToken.None);
        IReadOnlyDictionary<string, string> snapshot = ReplaySnapshot();

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_accepted");
        ReplaySnapshot().Should().Equal(snapshot);
    }

    [HostStateFact]
    public async Task Activation_refuses_rejected_replay_evidence_without_rewriting_store()
    {
        await RejectCurrentCandidateAsync();
        IReadOnlyDictionary<string, string> snapshot = ReplaySnapshot();

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_rejected");
        ReplaySnapshot().Should().Equal(snapshot);
    }

    [HostStateFact]
    public async Task Activation_refuses_signature_failure_before_images_or_executor()
    {
        await ImportAsync();
        _verifier.Result = false;

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.AddSingleton<IHostUpdateExecutor, ThrowingExecutor>();
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_signature_unverified");
        _imageVerifier.Requests.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Activation_refuses_policy_channel_mismatch()
    {
        await ImportAsync();
        await _host.ChangePolicyAsync("stable");

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("channel_mismatch_policy");
    }

    [HostStateFact]
    public async Task Activation_refuses_tampered_manifest_digest_before_images_or_executor()
    {
        await ImportAsync();
        File.WriteAllText(
            Path.Combine(_staging, HostUpdateOfflineAdmission.ManifestName),
            Encoding.UTF8.GetString(_manifest).Replace(
                "\"minimumUpdaterVersion\":\"0.0.0\"",
                "\"minimumUpdaterVersion\":\"0.0.1\"",
                StringComparison.Ordinal));

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.AddSingleton<IHostUpdateExecutor, ThrowingExecutor>();
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("staging_verification_mismatch");
        _imageVerifier.Requests.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Activation_refuses_replayed_lower_sequence()
    {
        await ImportAsync();
        StageManifest(json => json
            .Replace("1.2.3-insider.42", "1.2.3-insider.41", StringComparison.Ordinal)
            .Replace("10020000300042", "10020000300041", StringComparison.Ordinal));

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_rejected");
    }

    [HostStateFact]
    public async Task Activation_refuses_superseded_imported_identity()
    {
        await ImportAsync();
        StageManifest(json => json
            .Replace("1.2.3-insider.42", "1.2.3-insider.43", StringComparison.Ordinal)
            .Replace("10020000300042", "10020000300043", StringComparison.Ordinal));
        await ImportAsync();
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineAdmission.ManifestName), _manifest);
        WriteRecord(Digest(_manifest));

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_superseded");
    }

    [HostStateFact]
    public async Task Activation_refuses_incomplete_platform_image_set_before_executor()
    {
        await ImportAsync();
        StageManifest(json => json.Replace(
            "\"api/linux-amd64\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\",",
            string.Empty,
            StringComparison.Ordinal));

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.AddSingleton<IHostUpdateExecutor, ThrowingExecutor>();
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Contain("staging_manifest_invalid");
        _imageVerifier.Requests.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Activation_refuses_local_image_verification_failure_and_preserves_prior_install()
    {
        await ImportAsync();
        _host.SeedInstalledState();
        string before = File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json"));
        _imageVerifier.Exception = new HostUpdatePreloadedImageVerificationException("api", "missing");

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.AddSingleton<IHostUpdateExecutor, ThrowingExecutor>();
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Contain("preloaded_image_unverified:api:missing");
        File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json")).Should().Be(before);
    }

    [HostStateFact]
    public async Task Activation_refuses_when_api_absence_cannot_be_proven_and_preserves_prior_install()
    {
        await ImportAsync();
        _host.SeedInstalledState();
        string before = File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json"));
        _safetyProbe.Error = "api_service_running";

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.AddSingleton<IHostUpdateExecutor, ThrowingExecutor>();
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("api_service_running");
        _safetyProbe.Requests.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json")).Should().Be(before);
    }

    [HostStateFact]
    public async Task Activation_reports_lock_held()
    {
        await ImportAsync();
        await using FileStream held = new(_host.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.LockHeld);
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("host_update_lock_held");
    }

    [HostStateFact]
    public async Task Activation_revalidates_replay_under_executor_lock_before_steps()
    {
        await ImportAsync();
        _host.SeedInstalledState(services: [.. CliHostFixture.SplitServices, "monolith"]);
        var steps = new RecordingExecutionSteps(null);
        var supersedingSafety = new SupersedingSafetyProbe(async () =>
        {
            StageManifest(json => json
                .Replace("1.2.3-insider.42", "1.2.3-insider.43", StringComparison.Ordinal)
                .Replace("10020000300042", "10020000300043", StringComparison.Ordinal));
            await ImportAsync();
            File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineAdmission.ManifestName), _manifest);
            WriteRecord(Digest(_manifest));
        });

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(supersedingSafety);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => steps);
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("reason").GetString().Should().Be("replay_superseded");
        steps.Calls.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Activation_consumes_replay_under_executor_lock_before_competing_import_can_supersede()
    {
        await ImportAsync();
        var steps = new RecordingExecutionSteps(new FileInstalledHostStateStore(Path.Combine(_host.StateDirectory, "installed-state.json")));
        RacingCompletionHook? hook = null;

        JsonElement activated = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => steps);
            services.RemoveAll<IHostUpdateExecutionCompletionHook>();
            services.AddScoped<IHostUpdateExecutionCompletionHook>(sp =>
            {
                hook = new RacingCompletionHook(sp);
                return hook;
            });
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        hook.Should().NotBeNull();
        hook!.CompetingLockWasBlocked.Should().BeTrue();
        HostUpdateReplayDecision replay = await HostUpdateOfflineActivation.ReadReplayDecisionAsync(
            _host.Configuration(),
            CurrentCandidate(),
            CancellationToken.None);
        replay.Disposition.Should().Be(HostUpdateReplayDisposition.Accepted);
    }

    [HostStateFact]
    public async Task Activation_rerun_after_completed_journal_finalizes_imported_replay_without_second_execution()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request);
        _host.SeedJournal(
            request,
            [
                (HostUpdateExecutionState.Verifying, "verify:after"),
                (HostUpdateExecutionState.Completed, "completed"),
            ],
            withBaseline: false);
        var steps = new RecordingExecutionSteps(null);

        JsonElement activated = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => steps);
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        steps.Calls.Should().BeEmpty();
        HostUpdateReplayDecision replay = await HostUpdateOfflineActivation.ReadReplayDecisionAsync(
            _host.Configuration(),
            CurrentCandidate(),
            CancellationToken.None);
        replay.Disposition.Should().Be(HostUpdateReplayDisposition.Accepted);
    }

    [HostStateFact]
    public async Task Activation_rerun_after_replay_consumed_and_verified_journal_finalizes_without_second_execution()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request);
        await HostUpdateOfflineActivation.MarkActivatedAsync(_host.Configuration(), CurrentCandidate(), CancellationToken.None);
        _host.SeedJournal(
            request,
            [
                (HostUpdateExecutionState.Preflight, "preflight:after"),
                (HostUpdateExecutionState.Draining, "drain:after"),
                (HostUpdateExecutionState.Fenced, "fence:after"),
                (HostUpdateExecutionState.BackedUp, "backup:after"),
                (HostUpdateExecutionState.Migrating, "migration:after"),
                (HostUpdateExecutionState.Applying, "apply:after"),
                (HostUpdateExecutionState.Verifying, "verify:after"),
            ],
            withBaseline: false);
        var steps = new RecordingExecutionSteps(null);

        JsonElement activated = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => steps);
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        steps.Calls.Should().BeEmpty();
        IReadOnlyList<HostUpdateExecutionActivity> activities = new FileHostUpdateExecutionJournal(_host.JournalPath).Read(request.ReleaseId);
        activities.Should().Contain(activity => activity.State == HostUpdateExecutionState.Completed && activity.Phase == "completed");
    }

    [HostStateFact]
    public async Task Activation_refuses_accepted_replay_without_verified_journal_proof()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request);
        await HostUpdateOfflineActivation.MarkActivatedAsync(_host.Configuration(), CurrentCandidate(), CancellationToken.None);
        _host.SeedJournal(
            request,
            [(HostUpdateExecutionState.Applying, "apply:after")],
            withBaseline: false);

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => new RecordingExecutionSteps(null));
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_accepted");
    }

    [HostStateFact]
    public async Task Activation_refuses_accepted_replay_with_completed_journal_for_different_binding()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request);
        await HostUpdateOfflineActivation.MarkActivatedAsync(_host.Configuration(), CurrentCandidate(), CancellationToken.None);
        HostUpdateExecutionRequest registryRequest = request with { ImageSourceMode = HostUpdateImageSourceMode.Registry };
        _host.SeedJournal(
            registryRequest,
            [
                (HostUpdateExecutionState.Verifying, "verify:after"),
                (HostUpdateExecutionState.Completed, "completed"),
            ],
            withBaseline: false);
        string journalBefore = File.ReadAllText(_host.JournalPath);
        IReadOnlyDictionary<string, string> replayBefore = ReplaySnapshot();
        var steps = new RecordingExecutionSteps(null);

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => steps);
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_accepted");
        steps.Calls.Should().BeEmpty();
        File.ReadAllText(_host.JournalPath).Should().Be(journalBefore);
        ReplaySnapshot().Should().Equal(replayBefore);
    }

    [HostStateFact]
    public async Task Activation_reports_completed_when_late_replay_consumption_fails_after_install()
    {
        await ImportAsync();
        var steps = new RecordingExecutionSteps(new FileInstalledHostStateStore(Path.Combine(_host.StateDirectory, "installed-state.json")));

        JsonElement activated = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => steps);
            services.RemoveAll<IHostUpdateExecutionCompletionHook>();
            services.AddScoped<IHostUpdateExecutionCompletionHook, FailingCompletionHook>();
        }));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        JsonElement result = activated.GetProperty("result");
        result.GetProperty("decision").GetString().Should().Be("activated");
        result.GetProperty("reason").GetString().Should().Be("activation_replay_record_failed");
        result.GetProperty("state").GetString().Should().Be(nameof(HostUpdateExecutionState.Completed));
    }

    [HostStateFact]
    public async Task Activation_refuses_completed_journal_before_reactivating()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        _host.SeedJournal(request, [(HostUpdateExecutionState.Completed, "completed")], withBaseline: false);

        JsonElement refused = Envelope(await RunAsync(Activate(), services =>
        {
            services.RemoveAll<IHostUpdateLocalImageVerifier>();
            services.AddSingleton(_imageVerifier);
            services.AddSingleton<IHostUpdateLocalImageVerifier>(sp => sp.GetRequiredService<FakeLocalImageVerifier>());
            services.RemoveAll<IHostUpdateOfflineActivationSafetyProbe>();
            services.AddSingleton<IHostUpdateOfflineActivationSafetyProbe>(_safetyProbe);
            services.RemoveAll<IHostUpdateExecutionSteps>();
            services.AddScoped<IHostUpdateExecutionSteps>(_ => new RecordingExecutionSteps(null));
        }));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("reason").GetString().Should().Be("activation_already_completed");
    }

    [Fact]
    public void Writer_absence_probe_allows_only_absent_exited_or_dead_writer_services()
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api"] = "api",
            ["slicer-host"] = "slicer-host",
            ["monolith"] = "printfarmer",
        };
        string output = """
            {"Service":"api","State":"exited"}
            {"Service":"slicer-host","State":"dead"}
            """;

        DockerComposeApiAbsenceProbe.ValidateComposeState(output, mappings).Should().BeNull();
        DockerComposeApiAbsenceProbe.ValidateComposeState("""{"Service":"printfarmer","State":"created"}""", mappings)
            .Should().Be("writer_service_active:monolith:created");
        DockerComposeApiAbsenceProbe.ValidateComposeState("not json", mappings)
            .Should().Be("writer_absence_unproven:compose_ps_unparseable");
    }

    private async Task ImportAsync()
    {
        JsonElement imported = Envelope(await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider", "--trusted-root", _trustedRoot, "--json"]));
        imported.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, imported.ToString());
        _verifier.Calls.Clear();
        _verifier.Options.Clear();
    }

    private string[] Activate() => ["offline-activate", "--staging", _staging, "--channel", "insider", "--trusted-root", _trustedRoot, "--json"];

    private Microsoft.Extensions.Configuration.IConfiguration IntegratedConfiguration(Action<Dictionary<string, string?>>? mutate = null) =>
        _host.Configuration(values =>
        {
            string[] services = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];
            for (int i = 0; i < services.Length; i++)
            {
                values[$"HostUpdateExecution:ActiveServiceIds:{i}"] = services[i];
            }

            values["HostUpdateExecution:MinimumFreeBytes"] = "1";
            values["HostUpdateExecution:DrainTimeoutSeconds"] = "1";
            values["HostUpdateExecution:DrainPollIntervalSeconds"] = "1";
            values["HostUpdateExecution:FencePollIntervalSeconds"] = "1";
            values["HostUpdateExecution:VerifyTimeoutSeconds"] = "1";
            values["HostUpdateExecution:VerifyPollIntervalSeconds"] = "1";
            values["Jwt:Key"] = new string('k', 32);
            values["Jwt:Issuer"] = "issuer";
            values["Jwt:Audience"] = "audience";
            mutate?.Invoke(values);
        });

    private static void UseIntegratedBoundaries(
        IServiceCollection services,
        IntegratedActivationProcessRunner runner,
        RecordingHealthHttpClientFactory http)
    {
        services.RemoveAll<IHostUpdateProcessRunner>();
        services.AddSingleton<IHostUpdateProcessRunner>(runner);
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(http);
        services.RemoveAll<IReadOnlyList<IFenceableWriter>>();
        services.AddSingleton<IReadOnlyList<IFenceableWriter>>(_ => Array.Empty<IFenceableWriter>());
        services.RemoveAll<IActiveWorkObservationPort>();
        services.AddScoped<IActiveWorkObservationPort, NoActiveWorkObservationPort>();
        services.RemoveAll<IHostUpdateBackupTarget>();
        services.RemoveAll<IReadOnlyList<IHostUpdateBackupTarget>>();
        services.AddScoped<IHostUpdateBackupTarget, TinyBackupTarget>();
        services.AddScoped<IReadOnlyList<IHostUpdateBackupTarget>>(sp => [.. sp.GetServices<IHostUpdateBackupTarget>()]);
        services.RemoveAll<IHostUpdateMigrationTarget>();
        services.RemoveAll<IReadOnlyList<IHostUpdateMigrationTarget>>();
        services.AddScoped<IHostUpdateMigrationTarget>(sp => new DockerBackedMigrationTarget(
            "AppDbContext",
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            sp.GetRequiredService<HostUpdateTargetImageMigrationRunner>()));
        services.AddScoped<IHostUpdateMigrationTarget>(sp => new DockerBackedMigrationTarget(
            "SlicerDbContext",
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            sp.GetRequiredService<HostUpdateTargetImageMigrationRunner>()));
        services.AddScoped<IReadOnlyList<IHostUpdateMigrationTarget>>(sp => [.. sp.GetServices<IHostUpdateMigrationTarget>()]);
    }

    private async Task WriteInstalledStateAsync(HostUpdateExecutionRequest request)
    {
        await new FileInstalledHostStateStore(Path.Combine(_host.StateDirectory, "installed-state.json"))
            .WriteAsync(new InstalledHostState(
                request.ReleaseId,
                request.ManifestDigest,
                request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal),
                string.Join('+', request.Targets.Select(t => t.ServiceId).Order(StringComparer.Ordinal)),
                DateTimeOffset.UtcNow,
                request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal)),
                CancellationToken.None);
    }

    private static bool PullNever(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], "--pull", StringComparison.Ordinal))
            {
                return string.Equals(arguments[index + 1], "never", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private void StageManifest(Func<string, string> edit)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(_manifest)));
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineAdmission.ManifestName), bytes);
        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(Encoding.UTF8.GetString(bytes));
        WriteRecord(Digest(bytes), manifest.Channel, manifest.Version);
    }

    private void WriteRecord(string manifestDigest)
    {
        SignedUpdateManifest manifest = SignedUpdateManifestValidator.Parse(Encoding.UTF8.GetString(_manifest));
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

    private Dictionary<string, string> ReplaySnapshot() =>
        Directory.EnumerateFiles(_host.HostStateRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) is "host-update-replay.json" or "replay-anchor.json" or "replay-anchor.journal")
            .ToDictionary(
                path => Path.GetFileName(path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private VerifiedHostUpdateCandidate CurrentCandidate()
    {
        HostUpdateOfflineAdmission.TryReadStaged(_staging, "insider", out HostUpdateOfflineAdmission.StagedRelease? staged, out string? error)
            .Should().BeTrue(error);
        string hostPlatform = HostUpdateOfflineActivation.HostPlatformFactory();
        return staged!.Candidate with
        {
            HostPlatform = hostPlatform,
            PlatformDigests = new HostUpdatePlatformDigests(
                staged.Manifest.PlatformDigests[HostUpdateOfflineAdmission.PlatformKey("api", hostPlatform)],
                staged.Manifest.PlatformDigests[HostUpdateOfflineAdmission.PlatformKey("frontend", hostPlatform)],
                staged.Manifest.PlatformDigests[HostUpdateOfflineAdmission.PlatformKey("slicer-host", hostPlatform)],
                staged.Manifest.PlatformDigests[HostUpdateOfflineAdmission.PlatformKey("printer-discovery", hostPlatform)],
                staged.Manifest.PlatformDigests[HostUpdateOfflineAdmission.PlatformKey("orcaslicer-worker", hostPlatform)],
                staged.Manifest.PlatformDigests[HostUpdateOfflineAdmission.PlatformKey("monolith", hostPlatform)]),
        };
    }

    private HostUpdateExecutionRequest RequestFromCurrentStaging()
    {
        VerifiedHostUpdateCandidate candidate = CurrentCandidate();
        return new HostUpdateExecutionRequest(
            candidate.ReleaseId,
            candidate.Sequence,
            candidate.ManifestDigest,
            candidate.SourceCommit,
            HostUpdateExecutionChannel.Insider,
            [
                new("api", candidate.HostPlatform, candidate.PlatformDigests.Api),
                new("frontend", candidate.HostPlatform, candidate.PlatformDigests.Frontend),
                new("slicer-host", candidate.HostPlatform, candidate.PlatformDigests.SlicerHost),
                new("printer-discovery", candidate.HostPlatform, candidate.PlatformDigests.PrinterDiscovery),
                new("orcaslicer-worker", candidate.HostPlatform, candidate.PlatformDigests.OrcaslicerWorker),
                new("monolith", candidate.HostPlatform, candidate.PlatformDigests.Monolith),
            ])
        {
            RequestId = "offline-activate:" + candidate.Identity[7..39],
            TrustRoot = candidate.TrustRoot,
            PolicyRevision = CliHostFixture.RecordedPolicy.PolicyRevision + 1,
            PolicyFingerprint = HostStateHostUpdateSchedulerSettings.ToSchedulerSettings(
                new HostUpdateAutomationPolicy(Channel: "insider", InsiderAcknowledged: true, Revision: 1)).Fingerprint,
            HostPlatform = candidate.HostPlatform,
            AuthorizationKind = HostUpdateAuthorizationKind.StandingPolicy,
            ImageSourceMode = HostUpdateImageSourceMode.PreloadedLocal,
        };
    }

    private async Task RejectCurrentCandidateAsync()
    {
        using var anchor = new FileHostUpdateReplayAnchor(Paths());
        using var store = new FileHostUpdateReplayStore(Paths().Root, anchor);
        _ = await store.DecideAsync(CurrentCandidate(), HostUpdateReplayIntent.Reject, CancellationToken.None);
    }

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

    private Task<CliRun> RunAsync(string[] args, Action<IServiceCollection>? configure = null) =>
        RunAsync(args, _host.Configuration(), configure);

    private static async Task<CliRun> RunAsync(string[] args, Microsoft.Extensions.Configuration.IConfiguration configuration, Action<IServiceCollection>? configure = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(args, () => configuration, output, error, configure, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    private static JsonElement Envelope(CliRun run)
    {
        using JsonDocument document = JsonDocument.Parse(run.Output);
        JsonElement root = document.RootElement.Clone();
        root.TryGetProperty("exitCode", out _).Should().BeTrue($"CLI output should be enveloped JSON; output: {run.Output}; error: {run.Error}; process exit: {run.ExitCode}");
        return root;
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);

    private sealed class FakeVerifier : ISignedReleaseVerifier
    {
        public bool Result { get; set; } = true;

        public Func<byte[], bool>? Reject { get; set; }

        public List<CosignVerifierOptions> Options { get; } = [];

        public List<Call> Calls { get; } = [];

        public Task<bool> VerifyAsync(ReadOnlyMemory<byte> manifest, ReadOnlyMemory<byte> bundle, string certificateIdentity, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(manifest.ToArray(), bundle.ToArray(), certificateIdentity));
            return Task.FromResult(Result && Reject?.Invoke(manifest.ToArray()) != true);
        }

        public sealed record Call(byte[] Manifest, byte[] Bundle, string Identity);
    }

    private sealed class FakeLocalImageVerifier : IHostUpdateLocalImageVerifier
    {
        public List<HostUpdateExecutionRequest> Requests { get; } = [];

        public Exception? Exception { get; set; }

        public Task VerifyTargetsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CompletingExecutor(IInstalledHostStateStore store) : IHostUpdateExecutor
    {
        public async Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default)
        {
            await store.WriteAsync(new InstalledHostState(
                request.ReleaseId,
                request.ManifestDigest,
                request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal),
                string.Join('+', request.Targets.Select(t => t.ServiceId).Order(StringComparer.Ordinal)),
                DateTimeOffset.UtcNow,
                request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal)), cancellationToken);
            return new HostUpdateExecutionResult(request.ReleaseId, HostUpdateExecutionState.Completed, null, []);
        }
    }

    private sealed class ThrowingExecutor : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("executor_should_not_run");
    }

    private sealed class FakeSafetyProbe : IHostUpdateOfflineActivationSafetyProbe
    {
        public string? Error { get; set; }

        public List<HostUpdateExecutionRequest> Requests { get; } = [];

        public Task<string?> ValidateSafeToExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Error);
        }
    }

    private sealed class SupersedingSafetyProbe(Func<Task> supersede) : IHostUpdateOfflineActivationSafetyProbe
    {
        private int _calls;

        public async Task<string?> ValidateSafeToExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await supersede().ConfigureAwait(false);
            }

            return null;
        }
    }

    private sealed class RecordingExecutionSteps(IInstalledHostStateStore? store) : IHostUpdateExecutionSteps
    {
        public List<string> Calls { get; } = [];

        public Task PreflightAsync(HostUpdateExecutionRequest request, CancellationToken ct) { Calls.Add("preflight"); return Task.CompletedTask; }
        public Task DrainAsync(HostUpdateExecutionRequest request, CancellationToken ct) { Calls.Add("drain"); return Task.CompletedTask; }
        public Task FenceAsync(HostUpdateExecutionRequest request, CancellationToken ct) { Calls.Add("fence"); return Task.CompletedTask; }
        public Task BackupAsync(HostUpdateExecutionRequest request, CancellationToken ct) { Calls.Add("backup"); return Task.CompletedTask; }
        public Task MigrateAsync(HostUpdateExecutionRequest request, CancellationToken ct) { Calls.Add("migration"); return Task.CompletedTask; }
        public Task ApplyAsync(HostUpdateExecutionRequest request, CancellationToken ct) { Calls.Add("apply"); return Task.CompletedTask; }
        public async Task VerifyAsync(HostUpdateExecutionRequest request, CancellationToken ct)
        {
            Calls.Add("verify");
            if (store is not null)
            {
                await store.WriteAsync(new InstalledHostState(
                    request.ReleaseId,
                    request.ManifestDigest,
                    request.Targets.ToDictionary(t => t.ServiceId, t => t.ChildDigest, StringComparer.Ordinal),
                    string.Join('+', request.Targets.Select(t => t.ServiceId).Order(StringComparer.Ordinal)),
                    DateTimeOffset.UtcNow,
                    request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal)), ct);
            }
        }
    }

    private sealed class FailingCompletionHook : IHostUpdateExecutionCompletionHook
    {
        public Task<string?> CompleteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("activation_replay_record_failed");
    }

    private sealed class RacingCompletionHook(IServiceProvider provider) : IHostUpdateExecutionCompletionHook
    {
        public bool CompetingLockWasBlocked { get; private set; }

        public async Task<string?> CompleteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
        {
            using IHostUpdateExecutionLease? competing = HostUpdateCli.TryAcquireLock(provider);
            CompetingLockWasBlocked = competing is null;
            InstalledHostState? installed = await provider.GetRequiredService<IInstalledHostStateStore>()
                .ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!HostUpdateOfflineActivation.InstalledStateMatches(request, installed))
            {
                return "activation_not_applied";
            }

            HostUpdateReplayDecision recorded = await HostUpdateOfflineActivation.MarkActivatedAsync(
                provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
                HostUpdateOfflineActivation.CandidateFromRequest(request),
                cancellationToken).ConfigureAwait(false);
            return recorded.Disposition == HostUpdateReplayDisposition.Accepted
                ? null
                : "activation_replay_record_failed";
        }
    }

    private sealed class DockerBackedMigrationTarget(
        string contextName,
        string providerName,
        HostUpdateTargetImageMigrationRunner runner) : IHostUpdateMigrationTarget
    {
        public string ContextName { get; } = contextName;

        public Task<string> GetProviderNameAsync(CancellationToken cancellationToken) => Task.FromResult(providerName);

        public Task<bool> HasPendingMigrationsAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            runner.HasPendingMigrationsAsync(request, ContextName, GetProviderNameAsync, cancellationToken);

        public Task<Farm.Infrastructure.Data.Migrations.DatabaseMigrationResult> MigrateAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken) =>
            runner.MigrateAsync(request, ContextName, GetProviderNameAsync, cancellationToken);

        public Task<string> GetConnectionStringFingerprintAsync(CancellationToken cancellationToken) =>
            Task.FromResult("same-database");
    }

    private sealed class NoActiveWorkObservationPort : IActiveWorkObservationPort
    {
        public Task<int> CountActiveAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class TinyBackupTarget : IHostUpdateBackupTarget
    {
        public string Name => "integrated-test";

        public bool IsExternallyOwned => false;

        public Task BackupAsync(string destinationDirectory, CancellationToken cancellationToken)
        {
            File.WriteAllText(Path.Combine(destinationDirectory, "backup.txt"), "ok");
            return Task.CompletedTask;
        }
    }

        private sealed class RecordingHealthHttpClientFactory(string expectedBaseUrl, bool succeeds) : IHttpClientFactory
        {
            public List<Uri> Requests { get; } = [];

            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient(new Handler(Requests, new Uri(expectedBaseUrl), succeeds));
                client.BaseAddress = new Uri(expectedBaseUrl);
                return client;
            }

            private sealed class Handler(List<Uri> requests, Uri expectedBase, bool succeeds) : HttpMessageHandler
            {
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    request.RequestUri.Should().NotBeNull();
                    Uri uri = request.RequestUri!;
                    requests.Add(uri);
                    uri.Host.Should().Be(expectedBase.Host);
                    string body = succeeds
                        ? """{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}"""
                        : """{"status":"Unhealthy","results":{"comprehensive":{"status":"Unhealthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}""";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    });
                }
            }
        }

        private sealed class IntegratedActivationProcessRunner(bool healthSucceeds) : IHostUpdateProcessRunner
        {
            private static readonly Dictionary<string, (string Compose, string Repository, string Digest)> Services = new(StringComparer.Ordinal)
            {
                ["api"] = ("api", "ghcr.io/olyforge3d/printfarmer-api", "sha256:" + new string('a', 64)),
                ["frontend"] = ("frontend", "ghcr.io/olyforge3d/printfarmer-frontend", "sha256:" + new string('b', 64)),
                ["slicer-host"] = ("slicer-host", "ghcr.io/olyforge3d/printfarmer-slicer-host", "sha256:" + new string('c', 64)),
                ["printer-discovery"] = ("printer-discovery", "ghcr.io/olyforge3d/printfarmer-printer-discovery", "sha256:" + new string('d', 64)),
                ["orcaslicer-worker"] = ("orcaslicer-worker", "ghcr.io/olyforge3d/printfarmer-orcaslicer-worker", "sha256:" + new string('e', 64)),
                ["monolith"] = ("printfarmer", "ghcr.io/olyforge3d/printfarmer-monolith", "sha256:" + new string('f', 64)),
            };

            public List<ProcessCall> Calls { get; } = [];

            private readonly Dictionary<string, string> _digestsByService = new(StringComparer.Ordinal);

            public IEnumerable<ProcessCall> ComposeUpCalls => Calls.Where(call =>
                call.Arguments.Count >= 3 &&
                string.Equals(call.Arguments[0], "compose", StringComparison.Ordinal) &&
                call.Arguments.Contains("up"));

            public IEnumerable<ProcessCall> MigrationRunCalls => Calls.Where(call =>
                call.Arguments.Count > 0 &&
                string.Equals(call.Arguments[0], "run", StringComparison.Ordinal) &&
                call.Arguments.Contains("--host-update-migration"));

            public bool ContainsDockerCommand(params string[] tokens) =>
                Calls.Any(call => tokens.All(token => call.Arguments.Contains(token, StringComparer.Ordinal)));

            Task<HostUpdateProcessResult> IHostUpdateProcessRunner.RunAsync(
                string fileName,
                IReadOnlyList<string> arguments,
                TimeSpan timeout,
                CancellationToken cancellationToken,
                IReadOnlyDictionary<string, string>? environment)
            {
                var call = new ProcessCall(fileName, [.. arguments], environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment, StringComparer.Ordinal));
                Calls.Add(call);
                FailOnForbidden(arguments);

                if (!fileName.EndsWith("docker", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.EndsWith("sqlite3", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HostUpdateProcessResult(1, string.Empty, "unexpected executable"));
                }

                if (fileName.EndsWith("sqlite3", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HostUpdateProcessResult(0, string.Empty, string.Empty));
                }

                if (arguments is ["version", "--format", "{{.Server.Version}}"])
                {
                    return Task.FromResult(new HostUpdateProcessResult(0, "25.0.0", string.Empty));
                }

                if (arguments.Count > 0 && string.Equals(arguments[0], "compose", StringComparison.Ordinal) && arguments.Contains("ps"))
                {
                    string output = string.Join('\n', Services.Values.Select(service => $$"""{"Service":"{{service.Compose}}","State":"exited"}"""));
                    return Task.FromResult(new HostUpdateProcessResult(0, output, string.Empty));
                }

                if (arguments.Count > 0 && string.Equals(arguments[0], "compose", StringComparison.Ordinal) && arguments.Contains("up"))
                {
                    arguments.Should().Contain("--no-build");
                    PullNever(arguments).Should().BeTrue();
                    return Task.FromResult(new HostUpdateProcessResult(0, string.Empty, string.Empty));
                }

                if (arguments.Count >= 5 &&
                    string.Equals(arguments[0], "image", StringComparison.Ordinal) &&
                    string.Equals(arguments[1], "inspect", StringComparison.Ordinal) &&
                    string.Equals(arguments[^1], "{{json .}}", StringComparison.Ordinal))
                {
                    return Task.FromResult(LocalImageInspectJson(arguments[2]));
                }

                if (arguments.Count >= 5 &&
                    string.Equals(arguments[0], "image", StringComparison.Ordinal) &&
                    string.Equals(arguments[1], "inspect", StringComparison.Ordinal) &&
                    string.Equals(arguments[^2], "{{index .RepoDigests 0}}", StringComparison.Ordinal))
                {
                    string imageRef = arguments[^1];
                    string? serviceId = Services.Keys.FirstOrDefault(id => imageRef.EndsWith(id, StringComparison.Ordinal));
                    if (serviceId is not null)
                    {
                        (string _, string repository, string fallbackDigest) = Services[serviceId];
                        string digest = _digestsByService.TryGetValue(serviceId, out string? learned) ? learned : fallbackDigest;
                        return Task.FromResult(new HostUpdateProcessResult(0, repository + "@" + digest, string.Empty));
                    }
                }

                if (arguments.Count >= 5 &&
                    string.Equals(arguments[0], "container", StringComparison.Ordinal) &&
                    string.Equals(arguments[1], "inspect", StringComparison.Ordinal))
                {
                    string container = arguments[^1];
                    string? serviceId = Services.Keys.FirstOrDefault(id => container.Contains(Services[id].Compose, StringComparison.Ordinal));
                    return Task.FromResult(serviceId is null
                        ? new HostUpdateProcessResult(1, string.Empty, "missing")
                        : new HostUpdateProcessResult(0, "image-ref-" + serviceId, string.Empty));
                }

                if (arguments.Count > 0 && string.Equals(arguments[0], "run", StringComparison.Ordinal))
                {
                    PullNever(arguments).Should().BeTrue();
                    int marker = -1;
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        if (string.Equals(arguments[i], "--host-update-migration", StringComparison.Ordinal))
                        {
                            marker = i;
                            break;
                        }
                    }

                    if (marker >= 0 && marker + 3 < arguments.Count)
                    {
                        string context = arguments[marker + 1];
                        string operation = arguments[marker + 2];
                        string output = operation == "probe"
                            ? $"HOST_UPDATE_MIGRATION_PENDING:{context}:1"
                            : $"HOST_UPDATE_MIGRATION_APPLIED:{context}:202609250001";
                        return Task.FromResult(new HostUpdateProcessResult(0, output, string.Empty));
                    }
                }

                return Task.FromResult(healthSucceeds
                    ? new HostUpdateProcessResult(0, string.Empty, string.Empty)
                    : new HostUpdateProcessResult(1, string.Empty, "scripted failure"));
            }

            private HostUpdateProcessResult LocalImageInspectJson(string imageReference)
            {
                string? serviceId = null;
                foreach ((string id, (string _, string repository, string _)) in Services)
                {
                    string prefix = repository + "@";
                    if (imageReference.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        serviceId = id;
                        _digestsByService[id] = imageReference[prefix.Length..];
                        break;
                    }
                }

                if (serviceId is null)
                {
                    return new HostUpdateProcessResult(1, string.Empty, "missing");
                }

                string json = JsonSerializer.Serialize(new
                {
                    RepoDigests = new[] { imageReference },
                    Os = "linux",
                    Architecture = "amd64",
                });
                return new HostUpdateProcessResult(0, json, string.Empty);
            }

            private static void FailOnForbidden(IReadOnlyList<string> arguments)
            {
                if ((arguments.Count >= 2 && string.Equals(arguments[0], "image", StringComparison.Ordinal) && string.Equals(arguments[1], "pull", StringComparison.Ordinal)) ||
                    arguments.Any(argument => argument is "build" or "login" or "push" or "buildx"))
                {
                    throw new InvalidOperationException("forbidden docker operation: " + string.Join(' ', arguments));
                }
            }

            public sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);
        }
}

[CollectionDefinition("HostUpdateOfflineVerifier", DisableParallelization = true)]
public sealed class HostUpdateOfflineVerifierCollection;
