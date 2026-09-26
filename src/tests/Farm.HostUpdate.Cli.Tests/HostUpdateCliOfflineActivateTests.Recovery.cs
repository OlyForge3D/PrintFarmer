using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>Offline prior-artifact recovery (issue #3082).</summary>
public sealed partial class HostUpdateCliOfflineActivateTests
{
    private const string PriorVersion = "1.2.3-insider.41";
    private const string PriorReleaseId = "insider:" + PriorVersion;
    private const string TargetReleaseId = "insider:1.2.3-insider.42";

    private static readonly Dictionary<string, string> PriorAmd64Digests = new(StringComparer.Ordinal)
    {
        ["api"] = "sha256:" + new string('b', 64),
        ["frontend"] = "sha256:" + new string('c', 64),
        ["slicer-host"] = "sha256:" + new string('d', 64),
        ["printer-discovery"] = "sha256:" + new string('e', 64),
        ["orcaslicer-worker"] = "sha256:" + new string('f', 64),
        ["monolith"] = "sha256:" + new string('1', 64),
    };

    [Theory]
    [InlineData(new[] { "offline-recover", "--staging", "{abs}", "--channel", "insider", "--trusted-root", "{abs}", "--release", "insider:1.2.3-insider.42", "--preview" }, "missing_option:--protected-backup")]
    [InlineData(new[] { "offline-recover", "--staging", "{abs}", "--channel", "insider", "--trusted-root", "{abs}", "--protected-backup", "relative.json", "--release", "insider:1.2.3-insider.42", "--preview" }, "protected_backup_not_absolute")]
    [InlineData(new[] { "offline-recover", "--staging", "{abs}", "--channel", "insider", "--trusted-root", "{abs}", "--protected-backup", "{abs}", "--preview" }, "missing_option:--release")]
    [InlineData(new[] { "offline-recover", "--staging", "{abs}", "--channel", "insider", "--trusted-root", "{abs}", "--protected-backup", "{abs}", "--release", "insider:1.2.3-insider.42" }, "exactly_one_of:--preview,--confirm")]
    [InlineData(new[] { "offline-recover", "--staging", "{abs}", "--channel", "insider", "--trusted-root", "{abs}", "--protected-backup", "{abs}", "--release", "insider:1.2.3-insider.42", "--confirm", "insider:1.2.3-insider.43" }, "confirm_mismatch")]
    [InlineData(new[] { "offline-recover", "--channel", "insider", "--trusted-root", "{abs}", "--protected-backup", "{abs}", "--release", "insider:1.2.3-insider.42", "--preview" }, "missing_option:--staging")]
    [InlineData(new[] { "recover", "--release", "insider:1.2.3-insider.42", "--preview", "--protected-backup", "{abs}" }, "unknown_option:--protected-backup")]
    public async Task Offline_recover_invalid_arguments_are_usage_errors(string[] args, string expected)
    {
        string absolutePath = Path.GetFullPath(Path.GetTempPath());
        CliRun run = await RunAsync(args.Select(arg => arg == "{abs}" ? absolutePath : arg).ToArray());

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expected);
    }

    [HostStateFact]
    public async Task Offline_recovery_restores_prior_bundled_set_with_network_denied()
    {
        string protectedBackup = StagePriorRecoverySet();
        await FailOfflineActivationAsync();
        string failedState = File.ReadAllText(InstalledStatePath());
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        var network = new NetworkDeniedHttpClientFactory();

        JsonElement preview = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: false), IntegratedConfiguration(), services =>
            UseNetworkDeniedBoundaries(services, runner, network)));
        preview.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, preview.ToString());
        File.ReadAllText(InstalledStatePath()).Should().Be(failedState, "preview never mutates the host");

        JsonElement confirmed = Envelope(await RunAsync(
            [.. OfflineRecover(protectedBackup, confirm: true), .. ReapprovalArguments(preview)],
            IntegratedConfiguration(),
            services => UseNetworkDeniedBoundaries(services, runner, network)));

        confirmed.GetProperty("exitCode").GetInt32().Should().BeOneOf(
            [HostUpdateCliExitCodes.Success, HostUpdateCliExitCodes.PhysicalReconciliationPending, HostUpdateCliExitCodes.FenceReleasePending],
            confirmed.ToString() + "\n" + preview.ToString() + "\n" + string.Join('\n', runner.Calls.Select(call => call.FileName + " " + string.Join(' ', call.Arguments))));
        confirmed.GetProperty("result").GetProperty("outcome").GetString().Should().BeOneOf(
            nameof(HostUpdateRecoveryOutcome.RolledBack), nameof(HostUpdateRecoveryOutcome.FenceReleasePending));
        InstalledHostState? state = await new FileInstalledHostStateStore(InstalledStatePath()).ReadAsync(CancellationToken.None);
        state!.ReleaseId.Should().Be(PriorReleaseId);
        state.ServiceDigests.Should().Equal(PriorAmd64Digests);
        File.ReadAllText(Path.Combine(RestoredTestTargetPath(), "backup.txt")).Should().Be("ok", "the protected backup is restored locally");
        runner.ContainsDockerCommand("image", "pull").Should().BeFalse();
        runner.ContainsDockerCommand("pull").Should().BeFalse();
        runner.ContainsDockerCommand("login").Should().BeFalse();
        runner.ComposeUpCalls.Should().NotBeEmpty();
        runner.ComposeUpCalls.Should().OnlyContain(call => call.Arguments.Contains("--no-build") && PullNever(call.Arguments));
        network.Refused.Should().BeEmpty("offline recovery must never make a non-loopback request");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_registry_mode_failed_request_before_any_change()
    {
        string protectedBackup = StagePriorRecoverySet();
        await WritePriorInstalledStateAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging() with { ImageSourceMode = HostUpdateImageSourceMode.Registry };
        WriteFailedJournal(request);
        string before = File.ReadAllText(InstalledStatePath());

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true)));

        AssertRefused(refused, "offline_recovery_requires_preloaded_request");
        File.ReadAllText(InstalledStatePath()).Should().Be(before);
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_when_installed_state_is_not_the_prior_set()
    {
        string protectedBackup = StagePriorRecoverySet();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request with { ReleaseId = PriorReleaseId, ManifestDigest = PriorDigest() });
        WriteFailedJournal(request);
        string before = File.ReadAllText(InstalledStatePath());

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: false)));

        AssertRefused(refused, "prior_installed_state_mismatch");
        File.ReadAllText(InstalledStatePath()).Should().Be(before);
    }

    [HostStateTheory]
    [InlineData("missing-service")]
    [InlineData("extra-service")]
    [InlineData("mismatched-platform")]
    public async Task Offline_recovery_refuses_installed_state_that_is_not_the_complete_prior_set(string variant)
    {
        string protectedBackup = StagePriorRecoverySet();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        WriteFailedJournal(request);
        Dictionary<string, string> digests = request.Targets.ToDictionary(t => t.ServiceId, t => PriorAmd64Digests[t.ServiceId], StringComparer.Ordinal);
        Dictionary<string, string> platforms = request.Targets.ToDictionary(t => t.ServiceId, t => t.Platform, StringComparer.Ordinal);
        string first = request.Targets[0].ServiceId;
        switch (variant)
        {
            case "missing-service":
                digests.Remove(first);
                platforms.Remove(first);
                break;
            case "extra-service":
                digests["unexpected"] = PriorAmd64Digests[first];
                platforms["unexpected"] = platforms[first];
                break;
            case "mismatched-platform":
                platforms[first] = platforms[first] == "linux-arm64" ? "linux-amd64" : "linux-arm64";
                break;
        }

        await new FileInstalledHostStateStore(InstalledStatePath()).WriteAsync(
            new InstalledHostState(PriorReleaseId, PriorDigest(), digests, string.Join('+', digests.Keys.Order(StringComparer.Ordinal)), DateTimeOffset.UtcNow, platforms),
            CancellationToken.None);
        string before = File.ReadAllText(InstalledStatePath());

        AssertRefused(Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: false))), "prior_installed_state_mismatch");
        AssertRefused(Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true))), "prior_installed_state_mismatch");
        File.ReadAllText(InstalledStatePath()).Should().Be(before);
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_failed_request_for_a_different_target()
    {
        string protectedBackup = StagePriorRecoverySet();
        await WritePriorInstalledStateAsync();
        WriteFailedJournal(RequestFromCurrentStaging() with { ManifestDigest = "sha256:" + new string('7', 64) });

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: false)));

        AssertRefused(refused, "offline_recovery_target_mismatch");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_tampered_prior_manifest_before_reading_host_state()
    {
        string protectedBackup = StagePriorRecoverySet();
        string priorPath = Path.Combine(_staging, HostUpdateOfflineRecovery.PriorManifestName);
        File.WriteAllText(priorPath, File.ReadAllText(priorPath).Replace(PriorAmd64Digests["api"], "sha256:" + new string('2', 64), StringComparison.Ordinal));

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true)));

        AssertRefused(refused, "prior_recovery_set_mismatch");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_prior_manifest_that_does_not_verify()
    {
        string protectedBackup = StagePriorRecoverySet();
        byte[] prior = File.ReadAllBytes(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorManifestName));
        _verifier.Reject = bytes => bytes.AsSpan().SequenceEqual(prior);

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true)));

        AssertRefused(refused, "prior_recovery_set_unverified");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_bundle_without_prior_set()
    {
        string protectedBackup = StagePriorRecoverySet();
        WriteRecord(Digest(_manifest));

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true)));

        AssertRefused(refused, "prior_recovery_set_missing");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_mismatched_protected_backup_reference()
    {
        string protectedBackup = StagePriorRecoverySet();
        File.WriteAllText(protectedBackup, JsonSerializer.Serialize(new
        {
            id = "backup-other",
            sha256 = new string('a', 64),
            locationClass = "host-local",
            releaseVersion = PriorVersion,
        }));

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true)));

        AssertRefused(refused, "protected_backup_mismatch");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_protected_backup_reference_with_extra_fields()
    {
        string protectedBackup = StagePriorRecoverySet();
        File.WriteAllText(protectedBackup, JsonSerializer.Serialize(new
        {
            id = "backup-41",
            sha256 = new string('a', 64),
            locationClass = "host-local",
            releaseVersion = PriorVersion,
            connectionString = "Host=db;Password=secret",
        }));

        JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: true)));

        AssertRefused(refused, "protected_backup_invalid");
    }

    [HostStateTheory]
    [InlineData("attached-volume")]
    [InlineData("external-storage")]
    public async Task Offline_recovery_refuses_protected_backup_held_by_an_external_owner(string locationClass)
    {
        string protectedBackup = StagePriorRecoverySet(locationClass);
        await WritePriorInstalledStateAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        WriteFailedJournal(request, migrated: true);
        string before = File.ReadAllText(InstalledStatePath());
        string journalBefore = File.ReadAllText(_host.JournalPath);
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        var network = new NetworkDeniedHttpClientFactory();

        foreach (bool confirm in new[] { false, true })
        {
            JsonElement refused = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm), IntegratedConfiguration(), services =>
                UseNetworkDeniedBoundaries(services, runner, network)));

            AssertRefused(refused, "protected_backup_owner_required");
            refused.ToString().Should().Contain("restore_through_backup_owner");
        }

        File.ReadAllText(InstalledStatePath()).Should().Be(before);
        File.ReadAllText(_host.JournalPath).Should().Be(journalBefore);
        runner.Calls.Should().BeEmpty("no restore, apply or pull may start");
        network.Refused.Should().BeEmpty("the external owner is never contacted");
    }

    [HostStateFact]
    public async Task Offline_recovery_refuses_release_that_is_not_the_staged_target()    {
        string protectedBackup = StagePriorRecoverySet();
        string[] args = OfflineRecover(protectedBackup, confirm: false);
        args[Array.IndexOf(args, "--release") + 1] = PriorReleaseId;

        JsonElement refused = Envelope(await RunAsync(args));

        AssertRefused(refused, "offline_recovery_release_mismatch");
    }

    [HostStateFact]
    public async Task Offline_recovery_stops_before_restoring_externally_owned_database()
    {
        string protectedBackup = StagePriorRecoverySet();
        await WritePriorInstalledStateAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        WriteFailedJournal(request, migrated: true);
        WriteBackupManifest(request.ReleaseId, "database");
        string before = File.ReadAllText(InstalledStatePath());
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        var network = new NetworkDeniedHttpClientFactory();
        Microsoft.Extensions.Configuration.IConfiguration configuration = IntegratedConfiguration(values =>
            values["HostUpdateExecution:DatabaseExternallyOwned"] = "true");

        JsonElement preview = Envelope(await RunAsync(OfflineRecover(protectedBackup, confirm: false), configuration, services =>
            UseNetworkDeniedBoundaries(services, runner, network)));

        preview.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.NeedsOperator, preview.ToString());
        preview.GetProperty("result").GetProperty("plan").GetProperty("detail").GetString()
            .Should().Be(HostUpdateRecoveryCoordinator.DatabaseExternallyOwnedStop);
        File.ReadAllText(InstalledStatePath()).Should().Be(before);
        runner.Calls.Should().BeEmpty("no restore, apply or pull may start");
        network.Refused.Should().BeEmpty();

        JsonElement confirmed = Envelope(await RunAsync(
            [.. OfflineRecover(protectedBackup, confirm: true), .. ReapprovalArguments(preview)],
            configuration,
            services => UseNetworkDeniedBoundaries(services, runner, network)));

        confirmed.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.NeedsOperator, confirmed.ToString());
        confirmed.ToString().Should().Contain(HostUpdateRecoveryCoordinator.DatabaseExternallyOwnedStop);
        File.ReadAllText(InstalledStatePath()).Should().Be(before);
        runner.Calls.Should().BeEmpty("confirm must also stop before any restore, apply or pull");
        network.Refused.Should().BeEmpty();
    }

    private static void AssertRefused(JsonElement envelope, string code)
    {
        envelope.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Refused, envelope.ToString());
        envelope.GetProperty("result").GetProperty("code").GetString().Should().Be(code);
    }

    private string[] OfflineRecover(string protectedBackup, bool confirm) =>
    [
        "offline-recover", "--staging", _staging, "--channel", "insider", "--trusted-root", _trustedRoot,
        "--protected-backup", protectedBackup, "--release", TargetReleaseId,
        .. confirm ? new[] { "--confirm", TargetReleaseId } : ["--preview"],
        "--json",
    ];

    private static string[] ReapprovalArguments(JsonElement preview)
    {
        JsonElement result = preview.GetProperty("result");
        if (result.TryGetProperty("drift", out JsonElement drift) &&
            drift.TryGetProperty("reapprovalToken", out JsonElement token) &&
            token.ValueKind == JsonValueKind.String)
        {
            return ["--reapprove-drift", token.GetString()!];
        }

        return [];
    }

    private async Task FailOfflineActivationAsync()
    {
        await ImportAsync();
        await WritePriorInstalledStateAsync();
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        var http = new RecordingHealthHttpClientFactory(_host.Configuration()["HostUpdateExecution:HealthCheckBaseUrl"] ?? "http://localhost:5245", succeeds: false);
        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UseIntegratedBoundaries(services, runner, http)));
        refused.GetProperty("result").GetProperty("state").GetString().Should().Be(nameof(HostUpdateExecutionState.RecoveryRequired), refused.ToString());
        runner.ContainsDockerCommand("image", "pull").Should().BeFalse();
    }

    private void UseNetworkDeniedBoundaries(IServiceCollection services, IntegratedActivationProcessRunner runner, NetworkDeniedHttpClientFactory network)
    {
        UseIntegratedBoundaries(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true));
        services.RemoveAll<IHttpClientFactory>();
        services.AddSingleton<IHttpClientFactory>(network);

        // The integrated test backup target is a plain owned directory; restore it locally.
        services.RemoveAll<IHostUpdateRestoreExecutor>();
        services.AddScoped<IHostUpdateRestoreExecutor>(_ => new ProcessHostUpdateRestoreExecutor(
            runner,
            new Dictionary<string, Func<string, HostUpdateRestoreCommand>>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["integrated-test"] = RestoredTestTargetPath() },
            TimeSpan.FromSeconds(5)));
    }

    private string RestoredTestTargetPath() => Path.Combine(_host.StateDirectory, "restored-integrated-test");

    private string InstalledStatePath() => Path.Combine(_host.StateDirectory, "installed-state.json");

    private Task WritePriorInstalledStateAsync()
    {
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        return WriteInstalledStateAsync(request with
        {
            ReleaseId = PriorReleaseId,
            ManifestDigest = PriorDigest(),
            Targets = [.. request.Targets.Select(target => target with { ChildDigest = PriorAmd64Digests[target.ServiceId] })],
        });
    }

    private string PriorDigest() => Digest(File.ReadAllBytes(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorManifestName)));

    /// <summary>Stages the prior signed manifest and a verification record binding it, returning the operator's backup reference.</summary>
    private string StagePriorRecoverySet(string locationClass = "host-local")
    {
        JsonNode prior = JsonNode.Parse(_manifest)!;
        prior["tag"] = "v" + PriorVersion;
        prior["version"] = PriorVersion;
        prior["sequence"] = 10020000300041L;
        JsonObject digests = prior["platformDigests"]!.AsObject();
        foreach ((string service, string digest) in PriorAmd64Digests)
        {
            digests[service + "/linux-amd64"] = digest;
        }

        byte[] priorBytes = Encoding.UTF8.GetBytes(prior.ToJsonString());
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorManifestName), priorBytes);
        File.WriteAllBytes(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorSignatureName), _signature);
        var backup = new { id = "backup-41", sha256 = new string('a', 64), locationClass, releaseVersion = PriorVersion };
        SignedUpdateManifest target = SignedUpdateManifestValidator.Parse(Encoding.UTF8.GetString(_manifest));
        string json = JsonSerializer.Serialize(new
        {
            schema = 1,
            decision = HostUpdateOfflineAdmission.VerifiedDecision,
            release = new { channel = target.Channel, version = target.Version },
            manifestDigest = Digest(_manifest),
            priorRecoverySet = new
            {
                mode = "packaged",
                release = new { tag = "v" + PriorVersion, version = PriorVersion, channel = "insider", sequence = 10020000300041L },
                manifestDigest = Digest(priorBytes),
                protectedBackup = backup,
            },
        });
        File.WriteAllText(Path.Combine(_staging, HostUpdateOfflineAdmission.VerificationName), json);
        string reference = Path.Combine(_host.Root, "protected-backup.json");
        File.WriteAllText(reference, JsonSerializer.Serialize(backup));
        return reference;
    }

    private void WriteFailedJournal(HostUpdateExecutionRequest request, bool migrated = false) =>
        _host.SeedJournal(
            request,
            [migrated ? (HostUpdateExecutionState.Migrating, "migration:before") : (HostUpdateExecutionState.Applying, "apply:before"),
                (HostUpdateExecutionState.RecoveryRequired, "failure")],
            baseline: null);

    private void WriteBackupManifest(string releaseId, string targetName)
    {
        string releaseDirectory = new([.. releaseId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        string run = Path.Combine(_host.Root, "backups", releaseDirectory, "20260925000000000");
        string file = Path.Combine(run, targetName, "dump");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        byte[] bytes = "database-dump"u8.ToArray();
        File.WriteAllBytes(file, bytes);
        var manifest = new HostUpdateBackupManifest(
            releaseId,
            DateTimeOffset.UtcNow.AddMinutes(-30),
            [targetName],
            [new HostUpdateBackupManifestFile(targetName + "/dump", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.LongLength)]);
        File.WriteAllText(Path.Combine(run, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private sealed class NetworkDeniedHttpClientFactory : IHttpClientFactory
    {
        public List<Uri> Refused { get; } = [];

        public HttpClient CreateClient(string name) => new(new Handler(Refused)) { BaseAddress = new Uri("http://localhost:5245") };

        private sealed class Handler(List<Uri> refused) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Uri uri = request.RequestUri!;
                if (!uri.IsLoopback)
                {
                    refused.Add(uri);
                    throw new HttpRequestException("network denied: " + uri.Host);
                }

                const string body = """{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}""";
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }
    }
}
