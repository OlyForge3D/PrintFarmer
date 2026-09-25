using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// End-to-end coverage for the host-local recovery CLI (issue #2980, first slice). Every test
/// runs with no API process at all: the CLI reads and writes only the durable host-update root.
/// </summary>
public sealed class HostUpdateCliTests : IDisposable
{
    private readonly CliHostFixture _host = new();

    public void Dispose() => _host.Dispose();

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
        lines[0] = lines[0].Replace("apply:before", "apply:tampered", StringComparison.Ordinal);
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
        _host.Snapshot().Should().BeEquivalentTo(before);
        File.Exists(_host.LockPath).Should().BeFalse();
    }

    [Fact]
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

        // A new process against the same root (no shared memory) must be a durable no-op.
        IReadOnlyDictionary<string, string> before = _host.Snapshot();
        CliRun second = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId]);

        second.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        second.Output.Should().Contain("outcome: RolledBack");
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
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
