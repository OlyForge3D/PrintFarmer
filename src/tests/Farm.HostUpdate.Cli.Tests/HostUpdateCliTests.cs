using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// End-to-end coverage for the host-local recovery CLI (issue #2980, first slice). Every test
/// runs with no API process at all: the CLI reads and writes only the durable host-update root.
/// </summary>
public sealed class HostUpdateCliTests : IDisposable, IAsyncLifetime
{
    private readonly CliHostFixture _host = new();

    public void Dispose() => _host.Dispose();

    public Task InitializeAsync() => _host.ProvisionPolicyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(new string[0], "missing_command")]
    [InlineData(new[] { "rollout" }, "unknown_command")]
    [InlineData(new[] { "status", "--json", "--json" }, "duplicate_option:--json")]
    [InlineData(new[] { "status", "--password", "x" }, "unknown_option:--password")]
    [InlineData(new[] { "status", "--release", "not-a-release" }, "invalid_release_id")]
    [InlineData(new[] { "status", "--preview" }, "unknown_option:--preview")]
    [InlineData(new[] { "recover", "--preview" }, "missing_option:--release")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3" }, "exactly_one_of:--preview,--confirm")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--preview", "--confirm", "stable:1.2.3" }, "exactly_one_of:--preview,--confirm")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm", "stable:1.2.4" }, "confirm_mismatch")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm" }, "missing_value:--confirm")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--request-id", "bad id;rm", "--preview" }, "invalid_request_id")]
    [InlineData(new[] { "help", "extra" }, "unexpected_argument")]
    public async Task Invalid_arguments_exit_with_usage_and_touch_nothing(string[] args, string expectedError)
    {
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(args);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expectedError);
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Help_prints_usage_and_exit_codes()
    {
        CliRun run = await RunAsync(["help"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        run.Output.Should().Contain("not rollout authorization").And.Contain("11 fence release pending");
    }

    [Fact]
    public async Task Unconfigured_root_is_configuration_unproven()
    {
        CliRun run = await RunAsync(["status", "--json"], _host.Configuration(v => v["HostUpdateExecution:RootDirectory"] = string.Empty));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("root_directory_not_visible");
    }

    [Fact]
    public async Task Relative_root_fails_options_validation_as_configuration_unproven()
    {
        CliRun run = await RunAsync(["status", "--json"], _host.Configuration(v => v["HostUpdateExecution:RootDirectory"] = "relative/root"));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("configuration_invalid");
    }

    [Fact]
    public async Task Malformed_config_file_is_configuration_unproven_with_a_json_envelope()
    {
        string config = Path.Combine(_host.Root, "malformed.json");
        await File.WriteAllTextAsync(config, "{ \"HostUpdateExecution\": ");

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(
            ["status", "--json"],
            () => new ConfigurationBuilder().AddJsonFile(config, optional: false, reloadOnChange: false).Build(),
            output,
            error,
            CancellationToken.None);
        var run = new CliRun(exitCode, output.ToString(), error.ToString());

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("code").GetString().Should().Be("configuration_unreadable");
        run.Output.Should().NotContain(_host.Root.Replace("\\", "\\\\", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_config_source_is_configuration_unproven()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(
            ["status"],
            () => throw new UnauthorizedAccessException("denied"),
            output,
            error,
            CancellationToken.None);

        exitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        output.ToString().Should().Contain("configuration_unreadable");
    }

    [Fact]
    public async Task Unconvertible_option_value_is_configuration_unproven()
    {
        CliRun run = await RunAsync(["status", "--json"], _host.Configuration(v => v["HostUpdateExecution:DrainTimeoutSeconds"] = "notanumber"));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("configuration_invalid");
    }

    [Fact]
    public async Task Usage_errors_are_reported_before_configuration_is_loaded()
    {
        bool loaded = false;
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(
            ["recover", "--release", CliHostFixture.ReleaseId],
            () =>
            {
                loaded = true;
                throw new InvalidDataException();
            },
            output,
            error,
            CancellationToken.None);

        exitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        loaded.Should().BeFalse();
    }

    [Fact]
    public async Task Process_entrypoint_maps_a_malformed_config_file_to_exit_3_json()
    {
        string config = Path.Combine(_host.Root, "malformed.json");
        await File.WriteAllTextAsync(config, "not json");

        (int exitCode, string stdout) = await RunProcessAsync("--config", config, "status", "--json");

        exitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        using JsonDocument document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        document.RootElement.GetProperty("result").GetProperty("code").GetString().Should().Be("configuration_unreadable");
    }

    [Fact]
    public async Task Process_entrypoint_maps_a_missing_config_file_to_exit_3_json()
    {
        string config = Path.Combine(_host.Root, "absent.json");

        (int exitCode, string stdout) = await RunProcessAsync("--config", config, "status", "--json");

        exitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        using JsonDocument document = JsonDocument.Parse(stdout);
        document.RootElement.GetProperty("result").GetProperty("code").GetString().Should().Be("configuration_unreadable");
    }

    [Fact]
    public async Task Process_entrypoint_maps_an_unreadable_config_file_to_exit_3_json()
    {
        string config = Path.Combine(_host.Root, "locked.json");
        await File.WriteAllTextAsync(config, "{}");

        // Hold an exclusive handle so the child cannot open the file (portable access denial).
        (int exitCode, string stdout) result;
        using (new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await RunProcessAsync("--config", config, "status", "--json");
        }

        if (!OperatingSystem.IsWindows())
        {
            // POSIX advisory locking does not block the reader; the file is then readable ({}),
            // so only assert the contract never degrades to a usage error.
            result.exitCode.Should().NotBe(HostUpdateCliExitCodes.Usage);
            return;
        }

        result.exitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        using JsonDocument document = JsonDocument.Parse(result.stdout);
        document.RootElement.GetProperty("result").GetProperty("code").GetString().Should().Be("configuration_unreadable");
    }

    [Fact]
    public async Task Process_entrypoint_rejects_a_relative_config_path_as_usage()
    {
        (int exitCode, string stdout) = await RunProcessAsync("--config", "relative.json", "status");

        exitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        stdout.Should().BeEmpty();
    }

    private static async Task<(int ExitCode, string Stdout)> RunProcessAsync(params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "Farm.HostUpdate.Cli.dll");
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath)
            ? hostPath
            : "dotnet";
        var start = new System.Diagnostics.ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Keep ambient HostUpdateExecution__* settings from influencing the child.
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("HostUpdateExecution__", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        _ = await stderr;
        return (process.ExitCode, await stdout);
    }

    [Fact]
    public async Task Missing_root_directory_is_configuration_unproven_and_is_not_created()
    {
        string missing = _host.Root + "-missing";

        CliRun run = await RunAsync(["status"], _host.Configuration(v => v["HostUpdateExecution:RootDirectory"] = missing));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        Directory.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public async Task Status_lists_releases_and_reports_uncertain_phases_without_the_api()
    {
        _host.SeedRecoveryRequired();

        CliRun list = await RunAsync(["status", "--json"]);
        CliRun detail = await RunAsync(["status", "--release", CliHostFixture.ReleaseId, "--json"]);

        list.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        JsonElement release = Envelope(list).GetProperty("result").GetProperty("releases")[0];
        release.GetProperty("releaseId").GetString().Should().Be(CliHostFixture.ReleaseId);
        release.GetProperty("state").GetString().Should().Be("RecoveryRequired");

        detail.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        JsonElement result = Envelope(detail).GetProperty("result");
        result.GetProperty("lockHeld").GetBoolean().Should().BeFalse();
        result.GetProperty("release").GetProperty("requestId").GetString().Should().Be(CliHostFixture.RequestId);
        result.GetProperty("release").GetProperty("uncertainPhases")[0].GetString().Should().Be("apply");
    }

    [Fact]
    public async Task Status_text_output_is_line_oriented()
    {
        _host.SeedRecoveryRequired();

        CliRun run = await RunAsync(["status", "--release", CliHostFixture.ReleaseId]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        run.Output.Should().Contain("exitCode: 0").And.Contain("release.state: RecoveryRequired").And.Contain("release.uncertainPhases[0]: apply");
    }

    [Fact]
    public async Task Unknown_release_is_no_history()
    {
        _host.SeedRecoveryRequired();

        CliRun status = await RunAsync(["status", "--release", "stable:9.9.9"]);
        CliRun preview = await RunAsync(["recover", "--release", "stable:9.9.9", "--preview"]);

        status.ExitCode.Should().Be(HostUpdateCliExitCodes.NoHistory);
        preview.ExitCode.Should().Be(HostUpdateCliExitCodes.NoHistory);
    }

    [Fact]
    public async Task Corrupt_journal_is_state_unreadable()
    {
        _host.SeedRecoveryRequired();
        string[] lines = File.ReadAllLines(_host.JournalPath);
        lines[1] = lines[1].Replace("apply:before", "apply:tampered", StringComparison.Ordinal);
        File.WriteAllLines(_host.JournalPath, lines);

        CliRun status = await RunAsync(["status", "--release", CliHostFixture.ReleaseId, "--json"]);
        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview"]);

        status.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable);
        Envelope(status).GetProperty("result").GetProperty("code").GetString().Should().StartWith("journal_");
        preview.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable);
    }

    [Fact]
    public async Task Held_execution_lock_is_reported_without_reading_or_rewriting_the_journal()
    {
        _host.SeedRecoveryRequired();
        File.WriteAllText(_host.JournalPath + ".staged", "in-flight");
        using IHostUpdateExecutionLease lease = new FileHostUpdateExecutionLock(_host.LockPath).Acquire(TimeSpan.FromSeconds(5), CancellationToken.None);

        CliRun status = await RunAsync(["status", "--json"]);
        CliRun confirm = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId]);

        status.ExitCode.Should().Be(HostUpdateCliExitCodes.LockHeld);
        Envelope(status).GetProperty("result").GetProperty("lockHeld").GetBoolean().Should().BeTrue();
        confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.LockHeld);
        File.Exists(_host.JournalPath + ".staged").Should().BeTrue();
        _host.ReadOutcome().Should().BeNull();
    }

    [Fact]
    public async Task Release_not_in_recovery_is_refused()
    {
        _host.SeedJournal(CliHostFixture.Request(), (HostUpdateExecutionState.Completed, "installed-state:after"));

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Refused);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be(HostUpdateRecoveryRequestResolver.NotInRecovery);
        _host.ReadOutcome().Should().BeNull();
    }

    [Fact]
    public async Task Mismatched_request_id_is_refused_before_side_effects()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--request-id", "other-request", "--confirm", CliHostFixture.ReleaseId, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Refused);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be(HostUpdateRecoveryRequestResolver.RequestMismatch);
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue();
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
    }

    [Fact]
    public async Task Preview_makes_no_writes_and_reports_the_plan()
    {
        _host.SeedRecoveryRequired();
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        // No installed state and apply may have started: the engine refuses to guess.
        run.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("plan").GetProperty("kind").GetString().Should().Be("NeedsOperator");
        result.GetProperty("plan").GetProperty("detail").GetString().Should().Be("prior_image_state_missing_after_apply_started");
        result.GetProperty("namespaceProofFailures").GetArrayLength().Should().Be(0);
        _host.Snapshot().Should().BeEquivalentTo(before);
        _host.ReadOutcome().Should().BeNull();
    }

    [Fact]
    public async Task Preview_reports_namespace_proof_failures_without_refusing()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        File.Delete(_host.DockerPath);

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("plan").GetProperty("kind").GetString().Should().Be("FenceReleaseOnly");
        result.GetProperty("namespaceProofFailures")[0].GetString().Should().Be("executable_missing:docker");
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("sqlite3")]
    [InlineData("compose")]
    [InlineData("database")]
    [InlineData("owned")]
    [InlineData("journal")]
    public async Task Confirm_fails_closed_when_the_namespace_is_unproven(string missing)
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        switch (missing)
        {
            case "docker":
                File.Delete(_host.DockerPath);
                break;
            case "sqlite3":
                File.Delete(_host.Sqlite3Path);
                break;
            case "compose":
                File.Delete(_host.ComposeFile);
                break;
            case "database":
                File.Delete(_host.DatabasePath);
                break;
            case "owned":
                Directory.Delete(Path.Combine(_host.Root, "owned", "app-data"));
                break;
            case "journal":
                File.Delete(_host.JournalPath);
                break;
        }

        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("namespace_unproven");
        _host.Snapshot().Should().BeEquivalentTo(before, "the seeded lock sentinel is not rewritten either");
    }

    [HostStateFact]
    public async Task Confirm_redrives_only_the_pending_fence_release_and_is_idempotent_across_restarts()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore|fence_release_failed:IOException");
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);

        CliRun first = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        first.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        Envelope(first).GetProperty("result").GetProperty("outcome").GetString().Should().Be("RolledBack");
        File.Exists(_host.AdmissionClosedPath).Should().BeFalse();
        HostUpdateRecoveryOutcomeRecord recorded = _host.ReadOutcome()!;
        recorded.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        recorded.Detail.Should().Be("coordinated_restore");

        // A new process against the same root (no shared memory) must be a durable no-op. The
        // coordinator still takes (and so rewrites) the execution lock, which carries no state.
        string lockKey = Path.GetRelativePath(_host.Root, _host.LockPath);
        var before = _host.Snapshot().Where(pair => pair.Key != lockKey).ToDictionary(StringComparer.Ordinal);
        CliRun second = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId]);

        second.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        second.Output.Should().Contain("outcome: RolledBack");
        _host.Snapshot().Where(pair => pair.Key != lockKey).Should().BeEquivalentTo(before);
    }

    [HostStateFact]
    public async Task Confirm_without_backup_or_prior_state_records_needs_operator()
    {
        _host.SeedRecoveryRequired();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator);
        HostUpdateRecoveryOutcomeRecord recorded = _host.ReadOutcome()!;
        recorded.Outcome.Should().Be(HostUpdateRecoveryOutcome.NeedsOperator);
        recorded.Detail.Should().Be("prior_image_state_missing_after_apply_started");
    }

    private Task<CliRun> RunAsync(string[] args) => RunAsync(args, _host.Configuration());

    // --- Issue #2998: drift reapproval and downtime preview -------------------------------------

    [Theory]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm", "stable:1.2.3", "--reapprove-drift" }, "missing_value:--reapprove-drift")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--preview", "--reapprove-drift", "drift-00000000000000000000000000000000" }, "reapprove_drift_requires_confirm")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm", "stable:1.2.3", "--reapprove-drift", "drift-XYZ" }, "invalid_reapproval_token")]
    [InlineData(new[] { "status", "--reapprove-drift", "drift-00000000000000000000000000000000" }, "unknown_option:--reapprove-drift")]
    public async Task Invalid_reapproval_arguments_exit_with_usage_and_touch_nothing(string[] args, string expectedError)
    {
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(args);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expectedError);
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Help_documents_the_drift_exit_code_and_option()
    {
        CliRun run = await RunAsync(["help"]);

        run.Output.Should().Contain("12 drift not reapproved").And.Contain("--reapprove-drift <token>");
    }

    [HostStateFact]
    public async Task Preview_without_drift_reports_no_reapproval()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        JsonElement drift = Envelope(run).GetProperty("result").GetProperty("drift");
        drift.GetProperty("items").GetArrayLength().Should().Be(0);
        drift.GetProperty("reapprovalRequired").GetBoolean().Should().BeFalse();
        drift.TryGetProperty("reapprovalToken", out _).Should().BeFalse("a null token is omitted");
        drift.GetProperty("configurationFingerprint").GetString().Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    [HostStateFact]
    public async Task Policy_change_since_authorization_is_reported_as_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        await _host.ChangePolicyAsync("insider");
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement drift = Envelope(run).GetProperty("result").GetProperty("drift");
        DriftCodes(drift).Should().BeEquivalentTo("policy_revision_drift", "policy_fingerprint_drift", "channel_drift");
        drift.GetProperty("reapprovalRequired").GetBoolean().Should().BeTrue();
        drift.GetProperty("reapprovalToken").GetString().Should().MatchRegex("^drift-[0-9a-f]{32}$");
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Unverifiable_policy_is_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");

        CliRun run = await RunAsync(
            ["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"],
            _host.Configuration(v => v["HostUpdates:HostState:Enabled"] = "false"));

        DriftCodes(Envelope(run).GetProperty("result").GetProperty("drift")).Should().Equal("policy_unverifiable");
    }

    [HostStateFact]
    public async Task Host_platform_change_since_authorization_is_drift()
    {
        string other = CliHostFixture.CurrentPlatform == "linux-arm64" ? "linux-amd64" : "linux-arm64";
        _host.SeedRecoveryRequired(CliHostFixture.Request(hostPlatform: other));
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement result = Envelope(run).GetProperty("result");
        DriftCodes(result.GetProperty("drift")).Should().Equal("host_platform_drift");
        result.GetProperty("identity").GetProperty("currentPlatform").GetString().Should().Be(CliHostFixture.CurrentPlatform);
        result.GetProperty("identity").GetProperty("target").GetProperty("hostPlatform").GetString().Should().Be(other);
    }

    [HostStateFact]
    public async Task Prior_state_rewritten_after_authorization_is_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedInstalledState(recordedAt: DateTimeOffset.UtcNow.AddHours(1));

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        DriftCodes(Envelope(run).GetProperty("result").GetProperty("drift")).Should().Equal("prior_state_changed_since_authorization");
    }

    // --- Issue #3047: authorization-time baselines ----------------------------------------------

    [HostStateFact]
    public async Task Prior_state_rewritten_with_a_backdated_timestamp_is_drift()
    {
        _host.SeedInstalledState(recordedAt: DateTimeOffset.UtcNow.AddDays(-1));
        _host.SeedRecoveryRequired();
        _host.SeedInstalledState(recordedAt: DateTimeOffset.UtcNow.AddDays(-2), releaseId: "stable:1.2.1");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement drift = Envelope(run).GetProperty("result").GetProperty("drift");
        DriftCodes(drift).Should().Equal("prior_state_changed_since_authorization");
        drift.GetProperty("items")[0].GetProperty("observed").GetString().Should().StartWith("stable:1.2.1@sha256:");
    }

    [HostStateFact]
    public async Task Prior_state_deleted_after_authorization_is_drift()
    {
        _host.SeedInstalledState();
        _host.SeedRecoveryRequired();
        File.Delete(Path.Combine(_host.StateDirectory, "installed-state.json"));

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement item = Envelope(run).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("prior_state_changed_since_authorization");
        item.GetProperty("observed").GetString().Should().Be(HostUpdateBaselineHashes.NoInstalledState);
    }

    [HostStateFact]
    public async Task Journal_without_a_baseline_requires_reapproval_and_keeps_the_timestamp_fallback()
    {
        _host.SeedRecoveryRequired(withBaseline: false);

        CliRun unrecorded = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        _host.SeedInstalledState(recordedAt: DateTimeOffset.UtcNow.AddHours(1));
        CliRun rewritten = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        DriftCodes(Envelope(unrecorded).GetProperty("result").GetProperty("drift")).Should().Equal("authorization_baseline_unrecorded");
        DriftCodes(Envelope(rewritten).GetProperty("result").GetProperty("drift"))
            .Should().Equal("authorization_baseline_unrecorded", "prior_state_changed_since_authorization");
    }

    [HostStateFact]
    public async Task Confirm_against_a_journal_without_a_baseline_is_refused_with_exit_12()
    {
        _host.SeedRecoveryRequired(withBaseline: false);
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun refused = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        refused.ExitCode.Should().Be(HostUpdateCliExitCodes.DriftUnapproved);
        Envelope(refused).GetProperty("result").GetProperty("details")[0].GetString().Should().Be("authorization_baseline_unrecorded");
        _host.Snapshot().Should().BeEquivalentTo(before);

        CliRun approved = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--reapprove-drift", await PreviewTokenAsync(), "--json"]);

        approved.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
    }

    [HostStateFact]
    public async Task Configuration_change_since_authorization_is_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        string authorized = _host.CurrentBaseline().ConfigurationFingerprint;
        File.WriteAllText(_host.ComposeFile, "services: { changed: {} }\n");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement drift = Envelope(run).GetProperty("result").GetProperty("drift");
        DriftCodes(drift).Should().Equal("configuration_drift");
        drift.GetProperty("items")[0].GetProperty("recorded").GetString().Should().Be(authorized);
        drift.GetProperty("items")[0].GetProperty("observed").GetString().Should().Be(drift.GetProperty("configurationFingerprint").GetString());
    }

    [HostStateFact]
    public async Task Trust_root_change_since_authorization_is_drift()
    {
        _host.SeedRecoveryRequired(baseline: _host.CurrentBaseline() with { TrustRootFingerprint = "sha256:" + new string('0', 64) });
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement item = Envelope(run).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("trust_root_drift");
        item.GetProperty("observed").GetString().Should().Be(HostUpdateTrustRoot.Fingerprint);
    }

    [HostStateFact]
    public async Task Unpinned_recorded_trust_root_is_drift()
    {
        _host.SeedRecoveryRequired(CliHostFixture.Request() with { TrustRoot = "retired-root" });
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement item = Envelope(run).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("trust_root_drift");
        item.GetProperty("recorded").GetString().Should().Be("retired-root");
    }

    [Fact]
    public void Baseline_is_read_only_from_the_leading_accepted_activities_with_a_known_schema()
    {
        HostUpdateExecutionRequest request = CliHostFixture.Request();
        var baseline = new HostUpdateAuthorizationBaseline(1, "none", "sha256:c", HostUpdateTrustRoot.Fingerprint);
        HostUpdateExecutionActivity Activity(HostUpdateExecutionState state, string phase, HostUpdateAuthorizationBaseline? recorded) =>
            new("a", request.ReleaseId, state, phase, DateTimeOffset.UtcNow) { AuthorizationBaseline = recorded };

        HostUpdateRecoveryDrift.AuthorizationBaseline(
        [
            Activity(HostUpdateExecutionState.Accepted, "accepted", baseline),
            Activity(HostUpdateExecutionState.Accepted, "accepted", null),
        ]).Should().Be(baseline, "the first accepted activity carries the authorization baseline");
        HostUpdateRecoveryDrift.AuthorizationBaseline(
        [
            Activity(HostUpdateExecutionState.Accepted, "accepted", null),
            Activity(HostUpdateExecutionState.Accepted, "accepted", baseline),
        ]).Should().BeNull("a legacy authorization must not be rebased by a baseline captured on resume");
        HostUpdateRecoveryDrift.AuthorizationBaseline(
        [
            Activity(HostUpdateExecutionState.Accepted, "accepted", null),
            Activity(HostUpdateExecutionState.Preflight, "preflight:before", null),
            Activity(HostUpdateExecutionState.Accepted, "accepted", baseline),
        ]).Should().BeNull("a baseline journaled after execution started does not describe the authorization");
        HostUpdateRecoveryDrift.AuthorizationBaseline(
        [
            Activity(HostUpdateExecutionState.Accepted, "accepted", baseline with { SchemaVersion = 2, ManifestBinding = "sha256:d" }),
        ]).Should().NotBeNull("schema 2 carries the manifest binding");
        HostUpdateRecoveryDrift.AuthorizationBaseline(
        [
            Activity(HostUpdateExecutionState.Accepted, "accepted", baseline with { SchemaVersion = 3 }),
        ]).Should().BeNull("an unknown schema is treated as unrecorded");
    }

    [HostStateFact]
    public async Task Manifest_binding_is_journaled_and_unchanged_binding_is_not_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        _host.CurrentBaseline().ManifestBinding.Should().Be(CliHostFixture.TargetManifestDigest);
        DriftCodes(Envelope(run).GetProperty("result").GetProperty("drift")).Should().BeEmpty();
        _host.Snapshot().Should().BeEquivalentTo(before, "the binding is read without writing the database");
    }

    [HostStateFact]
    public async Task Changed_manifest_binding_is_drift_and_rebinds_the_token()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        string changedDigest = "sha256:" + new string('c', 64);
        _host.SeedManifestBinding(changedDigest);

        CliRun first = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        _host.SeedManifestBinding("sha256:" + new string('e', 64));
        CliRun second = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement drift = Envelope(first).GetProperty("result").GetProperty("drift");
        DriftCodes(drift).Should().Equal("manifest_binding_drift");
        drift.GetProperty("items")[0].GetProperty("recorded").GetString().Should().Be(CliHostFixture.TargetManifestDigest);
        drift.GetProperty("items")[0].GetProperty("observed").GetString().Should().Be(changedDigest);
        Envelope(second).GetProperty("result").GetProperty("drift").GetProperty("reapprovalToken").GetString()
            .Should().NotBe(drift.GetProperty("reapprovalToken").GetString(), "the token binds the observed manifest binding");
    }

    [HostStateFact]
    public async Task Deleted_manifest_binding_is_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        _host.SeedManifestBinding(null);

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement item = Envelope(run).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("manifest_binding_drift");
        item.GetProperty("observed").GetString().Should().Be(ReadOnlyHostUpdateManifestBindingReader.NoBinding);
    }

    [HostStateFact]
    public Task Corrupt_manifest_binding_is_drift_never_no_drift() => AssertUnreadableBindingIsDriftAsync("corrupt");

    [HostStateFact]
    public Task Missing_database_is_manifest_binding_drift_never_no_drift() => AssertUnreadableBindingIsDriftAsync("missing-database");

    private async Task AssertUnreadableBindingIsDriftAsync(string failure)
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        if (failure == "corrupt")
        {
            _host.SeedManifestBinding(null, rawJson: "{not json");
        }
        else
        {
            File.Delete(_host.DatabasePath);
        }

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement item = Envelope(run).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("manifest_binding_drift");
        item.GetProperty("observed").GetString().Should().StartWith("unreadable:");
        run.Output.Should().NotContain(_host.DatabasePath, "provider errors are reduced to their type");
    }

    [HostStateTheory]
    [InlineData("none")]
    [InlineData("sha256:abc")]
    [InlineData("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("Password=hunter2-secret-like-text")]
    public async Task Non_canonical_persisted_digest_is_unreadable_drift_and_never_emitted(string persisted)
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        _host.SeedManifestBinding(persisted);

        CliRun json = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        CliRun text = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview"]);

        JsonElement item = Envelope(json).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("manifest_binding_drift");
        item.GetProperty("observed").GetString().Should().Be("unreadable:InvalidDataException");
        if (persisted != ReadOnlyHostUpdateManifestBindingReader.NoBinding)
        {
            json.Output.Should().NotContain(persisted);
            text.Output.Should().NotContain(persisted);
        }
    }

    [HostStateTheory]
    [InlineData("corrupt")]
    [InlineData("missing-database")]
    public async Task Unreadable_binding_after_rollback_is_reported_and_confirm_stays_a_durable_no_op(string failure)
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.RolledBack, "coordinated_restore");
        if (failure == "corrupt")
        {
            _host.SeedManifestBinding(null, rawJson: "{not json");
        }
        else
        {
            File.Delete(_host.DatabasePath);
        }

        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        CliRun confirm = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        JsonElement drift = Envelope(preview).GetProperty("result").GetProperty("drift");
        DriftCodes(drift).Should().Equal("manifest_binding_drift");
        drift.GetProperty("items")[0].GetProperty("observed").GetString().Should().StartWith("unreadable:");
        if (failure == "corrupt")
        {
            confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
            Envelope(confirm).GetProperty("result").GetProperty("outcome").GetString().Should().Be("RolledBack");
        }
        else
        {
            // Confirm independently proves the database path exists before acting.
            confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        }
    }

    [HostStateFact]
    public async Task Unchanged_binding_after_rollback_is_not_drift()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.RolledBack, "coordinated_restore");

        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        DriftCodes(Envelope(preview).GetProperty("result").GetProperty("drift")).Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Schema_1_baseline_reports_the_manifest_binding_as_unrecorded_drift()
    {
        HostUpdateAuthorizationBaseline current = _host.CurrentBaseline();
        _host.SeedRecoveryRequired(baseline: current with { SchemaVersion = 1, ManifestBinding = null });
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement item = Envelope(run).GetProperty("result").GetProperty("drift").GetProperty("items")[0];
        item.GetProperty("code").GetString().Should().Be("manifest_binding_drift");
        item.GetProperty("recorded").GetString().Should().Be("unrecorded");
        item.GetProperty("observed").GetString().Should().Be(CliHostFixture.TargetManifestDigest);
    }

    [HostStateFact]
    public async Task Confirm_with_unapproved_drift_is_refused_with_exit_12_before_side_effects()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        await _host.ChangePolicyAsync();
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.DriftUnapproved).And.Be(12);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("code").GetString().Should().Be("drift_reapproval_required");
        result.GetProperty("details").EnumerateArray().Select(e => e.GetString()).Should().Contain("policy_revision_drift");
        run.Output.Should().NotContain("drift-", "the token must be read from --preview, never offered by the refusal");
        _host.Snapshot().Should().BeEquivalentTo(before);
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
    }

    [HostStateFact]
    public async Task Confirm_with_a_stale_token_is_refused()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        await _host.ChangePolicyAsync();
        string token = await PreviewTokenAsync();
        await _host.ChangePolicyAsync("insider");
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--reapprove-drift", token, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.DriftUnapproved);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("drift_reapproval_mismatch");
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [HostStateFact]
    public async Task Confirm_with_a_token_when_nothing_drifted_is_refused()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(
            ["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--reapprove-drift", "drift-" + new string('0', 32), "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.DriftUnapproved);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("drift_reapproval_unexpected");
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [HostStateFact]
    public async Task Confirm_with_the_previewed_token_proceeds()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        await _host.ChangePolicyAsync();
        string token = await PreviewTokenAsync();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--reapprove-drift", token, "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        File.Exists(_host.AdmissionClosedPath).Should().BeFalse();
    }

    [HostStateFact]
    public async Task Preview_reports_image_only_identity_downtime_and_writer_fence_without_writes()
    {
        _host.SeedInstalledState();
        _host.SeedRecoveryRequired();
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("plan").GetProperty("kind").GetString().Should().Be("ImageOnlyRollback");

        JsonElement identity = result.GetProperty("identity");
        identity.GetProperty("prior").GetProperty("releaseId").GetString().Should().Be("stable:1.2.2");
        identity.GetProperty("target").GetProperty("releaseId").GetString().Should().Be(CliHostFixture.ReleaseId);
        identity.GetProperty("target").GetProperty("manifestDigest").GetString().Should().Be(CliHostFixture.TargetManifestDigest);
        identity.GetProperty("target").GetProperty("channel").GetString().Should().Be("stable");
        identity.GetProperty("target").GetProperty("targets").GetArrayLength().Should().Be(6);

        var options = new HostUpdateExecutionOptions();
        JsonElement downtime = result.GetProperty("downtime");
        downtime.GetProperty("impact").GetString().Should().Be("service_restart");
        downtime.GetProperty("affectedServices").EnumerateArray().Select(e => e.GetString()).Should().Equal("api", "frontend", "monolith");
        downtime.GetProperty("timeoutBudgetSeconds").GetInt32()
            .Should().Be((4 * options.ApplyTimeoutSeconds) + options.VerifyTimeoutSeconds + options.VerifyPollIntervalSeconds, "three pulls plus one compose up, then verify");
        downtime.GetProperty("unboundedSteps").EnumerateArray().Select(e => e.GetString()).Should().Equal("health_check_final_pass");
        downtime.GetProperty("basis").GetString().Should().Be("sum_of_configured_timeouts_not_an_upper_bound");

        JsonElement fence = result.GetProperty("writerFence");
        fence.GetProperty("admissionClosed").GetBoolean().Should().BeTrue();
        fence.GetProperty("fencedWriters").GetArrayLength().Should().BeGreaterThan(0);
        fence.GetProperty("afterRecovery").GetString().Should().Be("writers_fenced_until_rollback_then_released");

        JsonElement recovery = result.GetProperty("recoveryEvidence");
        recovery.GetProperty("applyStarted").GetBoolean().Should().BeTrue();
        recovery.GetProperty("migrationStarted").GetBoolean().Should().BeFalse();
        result.GetProperty("backupEvidence").GetProperty("found").GetBoolean().Should().BeFalse();
        result.GetProperty("drift").GetProperty("items").GetArrayLength().Should().Be(0);

        _host.Snapshot().Should().BeEquivalentTo(before);
        _host.ReadOutcome().Should().BeNull();
    }

    [Fact]
    public async Task Preview_reports_coordinated_restore_downtime_and_backup_evidence()
    {
        _host.SeedInstalledState();
        _host.SeedJournal(
            CliHostFixture.Request(),
            (HostUpdateExecutionState.Migrating, "migration:before"),
            (HostUpdateExecutionState.RecoveryRequired, "failure"));
        _host.SeedBackup();
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("plan").GetProperty("kind").GetString().Should().Be("CoordinatedRestore");

        var options = new HostUpdateExecutionOptions();
        JsonElement downtime = result.GetProperty("downtime");
        downtime.GetProperty("impact").GetString().Should().Be("restore_and_service_restart");
        downtime.GetProperty("restoredTargets").EnumerateArray().Select(e => e.GetString()).Should().Equal("app-data", "database");
        int applyAndVerify = (4 * options.ApplyTimeoutSeconds) + options.VerifyTimeoutSeconds + options.VerifyPollIntervalSeconds;
        downtime.GetProperty("timeoutBudgetSeconds").GetInt32()
            .Should().Be(options.BackupTimeoutSeconds + applyAndVerify, "only the database target is a timed restore process");
        downtime.GetProperty("unboundedSteps").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("backup_checksum_verification", "owned_directory_copy", "health_check_final_pass");

        JsonElement backup = result.GetProperty("backupEvidence");
        backup.GetProperty("found").GetBoolean().Should().BeTrue();
        backup.GetProperty("releaseMatches").GetBoolean().Should().BeTrue();
        backup.GetProperty("fileCount").GetInt32().Should().Be(2);
        backup.GetProperty("totalBytes").GetInt64().Should().Be(6);
        backup.GetProperty("filesPresentWithRecordedLength").GetInt32().Should().Be(1);
        result.GetProperty("recoveryEvidence").GetProperty("migrationStarted").GetBoolean().Should().BeTrue();

        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(new[] { "database" }, false)]
    [InlineData(new[] { "app-data" }, true)]
    [InlineData(new[] { "app-data", "database" }, true)]
    public void Coordinated_restore_reports_directory_copy_only_when_a_directory_is_restored(string[] targets, bool expectDirectoryCopy)
    {
        var options = new HostUpdateExecutionOptions();
        options.OwnedDirectories["app-data"] = "/srv/app-data";
        var plan = new HostUpdateRecoveryPlan(HostUpdateRecoveryPlanKind.CoordinatedRestore, "restore", null, "/backups/run");
        var backup = new HostUpdateBackupManifest(CliHostFixture.ReleaseId, DateTimeOffset.UnixEpoch, targets, []);

        HostUpdateDowntimePreview downtime = HostUpdateRecoveryPreview.Downtime(plan, installed: null, backup, options);

        downtime.UnboundedSteps.Contains("owned_directory_copy").Should().Be(expectDirectoryCopy);
        downtime.UnboundedSteps.Should().Contain("backup_checksum_verification");
        downtime.TimeoutBudgetSeconds.Should().Be(targets.Count(t => t != "app-data") * options.BackupTimeoutSeconds);
    }

    [Fact]
    public async Task Preview_of_an_operator_only_plan_has_no_automatic_downtime_bound()
    {
        _host.SeedRecoveryRequired();

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        JsonElement downtime = Envelope(run).GetProperty("result").GetProperty("downtime");
        downtime.GetProperty("impact").GetString().Should().Be("operator_required");
        downtime.TryGetProperty("timeoutBudgetSeconds", out _).Should().BeFalse("no automatic path has no budget");
    }

    [Fact]
    public async Task Corrupt_installed_state_is_state_unreadable_for_preview_and_confirm()
    {
        _host.SeedRecoveryRequired();
        File.WriteAllText(Path.Combine(_host.StateDirectory, "installed-state.json"), "{not json");
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        CliRun confirm = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"]);

        preview.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable);
        Envelope(preview).GetProperty("result").GetProperty("code").GetString().Should().Be("installed_state_corrupt:json_invalid");
        confirm.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable);
        _host.Snapshot().Should().BeEquivalentTo(before);
        _host.ReadOutcome().Should().BeNull();
    }

    [Fact]
    public async Task Preview_takes_the_execution_lock_without_rewriting_it()
    {
        _host.SeedRecoveryRequired();
        string lockBefore = File.ReadAllText(_host.LockPath);
        DateTime writtenBefore = File.GetLastWriteTimeUtc(_host.LockPath);

        CliRun run = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.NeedsOperator);
        File.ReadAllText(_host.LockPath).Should().Be(lockBefore);
        File.GetLastWriteTimeUtc(_host.LockPath).Should().Be(writtenBefore);
    }

    [Fact]
    public async Task Approval_bound_store_refuses_a_state_that_changed_after_evaluation()
    {
        var inner = new MutableInstalledStateStore(Installed("stable:1.2.2"));
        var store = new ApprovalBoundInstalledHostStateStore(inner);
        store.Bind(HostUpdateRecoveryDrift.InstalledStateHash(await inner.ReadAsync(CancellationToken.None)));
        inner.State = Installed("stable:1.2.1");

        Func<Task> read = () => store.ReadAsync(CancellationToken.None);

        await read.Should().ThrowAsync<HostUpdateRecoveryApprovalStaleException>();
        (await store.ReadAsync(CancellationToken.None))!.ReleaseId.Should().Be("stable:1.2.1", "the binding is consumed by the decision read");
    }

    [Fact]
    public async Task Approval_bound_store_passes_the_evaluated_state_and_absence()
    {
        var inner = new MutableInstalledStateStore(Installed("stable:1.2.2"));
        var store = new ApprovalBoundInstalledHostStateStore(inner);
        store.Bind(HostUpdateRecoveryDrift.InstalledStateHash(await inner.ReadAsync(CancellationToken.None)));
        (await store.ReadAsync(CancellationToken.None))!.ReleaseId.Should().Be("stable:1.2.2");

        inner.State = null;
        store.Bind(HostUpdateRecoveryDrift.InstalledStateHash(null));
        (await store.ReadAsync(CancellationToken.None)).Should().BeNull();

        inner.State = Installed("stable:1.2.2");
        store.Bind(HostUpdateRecoveryDrift.InstalledStateHash(null));
        Func<Task> appeared = () => store.ReadAsync(CancellationToken.None);
        await appeared.Should().ThrowAsync<HostUpdateRecoveryApprovalStaleException>();
    }

    private static InstalledHostState Installed(string releaseId) =>
        new(
            releaseId,
            "sha256:" + new string('9', 64),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["api"] = "sha256:" + new string('1', 64) },
            "api",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class MutableInstalledStateStore(InstalledHostState? state) : IInstalledHostStateStore
    {
        public InstalledHostState? State { get; set; } = state;

        public Task<InstalledHostState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task WriteAsync(InstalledHostState state, CancellationToken cancellationToken)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private async Task<string> PreviewTokenAsync()
    {
        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        return Envelope(preview).GetProperty("result").GetProperty("drift").GetProperty("reapprovalToken").GetString()!;
    }

    private static string?[] DriftCodes(JsonElement drift) =>
        [.. drift.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("code").GetString())];

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
        JsonElement root = document.RootElement.Clone();
        root.GetProperty("exitCode").GetInt32().Should().Be(run.ExitCode);
        return root;
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);
}
