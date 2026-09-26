using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.HostUpdate.Cli.Tests;

[Collection("HostUpdateOfflineVerifier")]
public sealed class HostUpdateCliOfflineActivateTests : IDisposable, IAsyncLifetime
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
    [InlineData(new[] { "offline-activate", "--staging", "C:\\abs", "--channel", "beta" }, "invalid_channel")]
    public async Task Invalid_arguments_are_usage_errors(string[] args, string expected)
    {
        CliRun run = await RunAsync(args);

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
    public async Task Activation_refuses_without_imported_replay_evidence_and_preserves_prior_install()
    {
        _host.SeedInstalledState();
        string before = File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json"));

        JsonElement refused = Envelope(await RunAsync(Activate()));

        refused.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, refused.ToString());
        refused.GetProperty("result").GetProperty("code").GetString().Should().Be("replay_rejected");
        File.ReadAllText(Path.Combine(_host.StateDirectory, "installed-state.json")).Should().Be(before);
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

    private async Task ImportAsync()
    {
        JsonElement imported = Envelope(await RunAsync(["offline-admit", "--staging", _staging, "--channel", "insider", "--trusted-root", _trustedRoot, "--json"]));
        imported.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, imported.ToString());
        _verifier.Calls.Clear();
        _verifier.Options.Clear();
    }

    private string[] Activate() => ["offline-activate", "--staging", _staging, "--channel", "insider", "--trusted-root", _trustedRoot, "--json"];

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
}

[CollectionDefinition("HostUpdateOfflineVerifier", DisableParallelization = true)]
public sealed class HostUpdateOfflineVerifierCollection;
