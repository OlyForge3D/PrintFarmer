using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// Issue #2999: the CLI keeps admission fenced after a rollback until the operator records, with
/// the exact token printed by <c>--preview</c>, that every printer with an uncertain command
/// outcome was physically reconciled. Recovery never replays, cancels, or issues a printer command.
/// These run on every platform: a drift token is supplied only when the host reports drift.
/// </summary>
public sealed class HostUpdateCliPhysicalReconciliationTests : IDisposable, IAsyncLifetime
{
    private readonly CliHostFixture _host = new();

    public void Dispose() => _host.Dispose();

    public Task InitializeAsync() => _host.ProvisionPolicyAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--preview", "--printers-reconciled", "physical-0123456789abcdef0123456789abcdef" }, "printers_reconciled_requires_confirm")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm", "stable:1.2.3", "--printers-reconciled", "drift-0123456789abcdef0123456789abcdef" }, "invalid_printers_reconciled_token")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm", "stable:1.2.3", "--printers-reconciled", "physical-0123" }, "invalid_printers_reconciled_token")]
    [InlineData(new[] { "recover", "--release", "stable:1.2.3", "--confirm", "stable:1.2.3", "--printers-reconciled" }, "missing_value:--printers-reconciled")]
    [InlineData(new[] { "status", "--printers-reconciled", "physical-0123456789abcdef0123456789abcdef" }, "unknown_option:--printers-reconciled")]
    public async Task Invalid_reconciliation_arguments_exit_with_usage_and_touch_nothing(string[] args, string expectedError)
    {
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(args);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expectedError);
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Help_documents_the_reconciliation_exit_code_and_option()
    {
        CliRun run = await RunAsync(["help"]);

        run.Output.Should().Contain("13 physical printer reconciliation not recorded")
            .And.Contain("--printers-reconciled <token>")
            .And.Contain("never replays, cancels or issues a printer command");
    }

    [Fact]
    public async Task Preview_lists_each_printer_with_its_uncertain_outcomes_and_a_token_without_writes()
    {
        SeedPendingRelease();
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        JsonElement physical = await PreviewPhysicalAsync();

        physical.GetProperty("state").GetString().Should().Be("ready_to_record");
        physical.GetProperty("printerCount").GetInt32().Should().Be(3);
        physical.GetProperty("uncertainOutcomeCount").GetInt32().Should().Be(3);
        physical.GetProperty("reconciliationToken").GetString().Should().MatchRegex("^physical-[0-9a-f]{32}$");
        physical.GetProperty("replayPolicy").GetString().Should().Be("recovery_never_replays_or_issues_printer_commands");
        JsonElement[] printers = [.. physical.GetProperty("printers").EnumerateArray()];
        printers.Select(p => p.GetProperty("printerName").GetString()).Should().Equal("Printer A", "Printer B", "Idle printer");
        printers[0].GetProperty("uncertainOutcomes").EnumerateArray().Select(o => o.GetProperty("kind").GetString())
            .Should().Equal("physical_command_in_flight", "print_job_active");
        printers[1].GetProperty("uncertainOutcomes").EnumerateArray().Single().GetProperty("state").GetString().Should().Be("Unknown");
        printers[2].GetProperty("uncertainOutcomes").GetArrayLength().Should().Be(0);
        _host.Snapshot().Should().BeEquivalentTo(before, "preview reads the database read-only and writes nothing");
    }

    [Fact]
    public async Task Confirm_without_a_recorded_reconciliation_keeps_admission_fenced_with_exit_13()
    {
        SeedPendingRelease();
        (string? drift, _) = await TokensAsync();

        CliRun run = await ConfirmAsync(drift, physical: null);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.PhysicalReconciliationPending);
        JsonElement result = Envelope(run).GetProperty("result");
        result.GetProperty("outcome").GetString().Should().Be("FenceReleasePending");
        result.GetProperty("detail").GetString().Should().Be("coordinated_restore|physical_reconciliation_pending");
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue("writers stay fenced until reconciliation is recorded");
        _host.ReadOutcome()!.Outcome.Should().Be(HostUpdateRecoveryOutcome.FenceReleasePending);
        Directory.Exists(ReconciliationDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_with_the_previewed_token_records_reconciliation_and_reopens_admission()
    {
        SeedPendingRelease();
        (string? drift, string physical) = await TokensAsync();

        CliRun run = await ConfirmAsync(drift, physical);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        Envelope(run).GetProperty("result").GetProperty("outcome").GetString().Should().Be("RolledBack");
        File.Exists(_host.AdmissionClosedPath).Should().BeFalse();
        HostUpdateRecoveryOutcomeRecord outcome = _host.ReadOutcome()!;
        outcome.Outcome.Should().Be(HostUpdateRecoveryOutcome.RolledBack);
        outcome.Detail.Should().Be("coordinated_restore");

        string recordPath = Directory.EnumerateFiles(ReconciliationDirectory).Single();
        HostUpdatePhysicalReconciliationRecord record = JsonSerializer.Deserialize<HostUpdatePhysicalReconciliationRecord>(await File.ReadAllTextAsync(recordPath))!;
        record.IsIntact().Should().BeTrue();
        record.ReleaseId.Should().Be(CliHostFixture.ReleaseId);
        record.RequestId.Should().Be(CliHostFixture.RequestId);
        record.Printers.Select(p => p.PrinterId).Should().Contain([CliHostDatabase.PrinterA, CliHostDatabase.PrinterB, CliHostDatabase.IdlePrinter]);

        JsonElement after = await PreviewPhysicalAsync();
        after.GetProperty("state").GetString().Should().Be("complete");
    }

    [Fact]
    public async Task Recovery_never_replays_or_settles_uncertain_printer_commands()
    {
        SeedPendingRelease();
        byte[] databaseBefore = await File.ReadAllBytesAsync(_host.DatabasePath);
        (string? drift, string physical) = await TokensAsync();

        CliRun pending = await ConfirmAsync(drift, physical: null);
        CliRun released = await ConfirmAsync(drift, physical);

        pending.ExitCode.Should().Be(HostUpdateCliExitCodes.PhysicalReconciliationPending);
        released.ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
        (await File.ReadAllBytesAsync(_host.DatabasePath)).Should().Equal(databaseBefore, "the CLI never writes the application database");
        CliHostDatabase.ReadCommandState(_host.DatabasePath).Should().Be(
            (QueueOutboxEventStatus.Processing, DispatchAttemptOutcome.Unknown, true, Farm.Infrastructure.PrintJobStatus.Printing),
            "an uncertain command stays leased for the operator; recovery neither replays nor clears it");
        File.Exists(_host.DatabasePath + "-wal").Should().BeFalse();
        File.Exists(_host.DatabasePath + "-shm").Should().BeFalse();
    }

    [Fact]
    public async Task A_token_for_a_changed_inventory_is_refused_with_exit_13_and_records_nothing()
    {
        SeedPendingRelease();
        (string? drift, string stale) = await TokensAsync();
        CliHostDatabase.SettleUnknownAttempt(_host.DatabasePath);
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await ConfirmAsync(drift, stale);

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.PhysicalReconciliationPending);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("physical_reconciliation_mismatch");
        _host.Snapshot().Should().BeEquivalentTo(before);

        (_, string fresh) = await TokensAsync();
        fresh.Should().NotBe(stale);
        (await ConfirmAsync(drift, fresh)).ExitCode.Should().Be(HostUpdateCliExitCodes.Success);
    }

    [Fact]
    public async Task A_token_before_the_rollback_is_durable_is_refused_before_side_effects()
    {
        _host.SeedRecoveryRequired();
        CliHostDatabase.SeedUncertainCommands(_host.DatabasePath);
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
        (string? drift, _) = await TokensAsync(expectPhysicalToken: false);
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await ConfirmAsync(drift, "physical-0123456789abcdef0123456789abcdef");

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Refused);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().Be("physical_reconciliation_not_ready");
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Preview_before_rollback_reports_the_inventory_without_a_token()
    {
        _host.SeedRecoveryRequired();
        CliHostDatabase.SeedUncertainCommands(_host.DatabasePath);

        JsonElement physical = await PreviewPhysicalAsync();

        physical.GetProperty("state").GetString().Should().BeOneOf("after_rollback", "operator_required");
        physical.TryGetProperty("reconciliationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Unreadable_inventory_blocks_recording_with_exit_4()
    {
        SeedPendingRelease();
        (string? drift, string physical) = await TokensAsync();
        File.WriteAllText(_host.DatabasePath, "not a database");

        JsonElement preview = await PreviewPhysicalAsync();
        CliRun run = await ConfirmAsync(drift, physical);

        preview.GetProperty("state").GetString().Should().Be("inventory_unavailable");
        run.ExitCode.Should().Be(HostUpdateCliExitCodes.StateUnreadable);
        Envelope(run).GetProperty("result").GetProperty("code").GetString().Should().StartWith("physical_inventory_unavailable:");
        File.Exists(_host.AdmissionClosedPath).Should().BeTrue();
        Directory.Exists(ReconciliationDirectory).Should().BeFalse();
    }

    [Fact]
    public void The_cli_service_graph_contains_no_printer_backend_client_or_plugin()
    {
        IServiceCollection services = HostUpdateCli.ConfigureServices(new ServiceCollection(), _host.Configuration(), TextWriter.Null);

        Type[] forbidden = [typeof(Farm.Infrastructure.Contracts.Printers.IBackendClient), typeof(Farm.Infrastructure.Services.Printers.IBackendClientFactory), typeof(Farm.Backend.Plugin.Core.IBackendClientPlugin)];
        foreach (ServiceDescriptor descriptor in services)
        {
            Type?[] types = [descriptor.ServiceType, descriptor.ImplementationType, descriptor.ImplementationInstance?.GetType()];
            foreach (Type? type in types.Where(t => t is not null))
            {
                forbidden.Should().NotContain(t => t.IsAssignableFrom(type), $"{descriptor.ServiceType} must not provide a printer backend");
                type!.Assembly.GetName().Name.Should().NotStartWith("Farm.Backend");
            }
        }

        typeof(HostUpdateCli).Assembly.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain(name => name!.StartsWith("Farm.Backend", StringComparison.Ordinal));
    }

    private string ReconciliationDirectory => Path.Combine(_host.StateDirectory, "physical-reconciliation");

    private void SeedPendingRelease()
    {
        _host.SeedRecoveryRequired();
        _host.SeedOutcome(HostUpdateRecoveryOutcome.FenceReleasePending, "coordinated_restore");
        CliHostDatabase.SeedUncertainCommands(_host.DatabasePath);
        File.WriteAllText(_host.AdmissionClosedPath, string.Empty);
    }

    private async Task<JsonElement> PreviewPhysicalAsync()
    {
        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        return Envelope(preview).GetProperty("result").GetProperty("physicalReconciliation");
    }

    /// <summary>The drift token (only when the host reports drift) and the physical token from one preview.</summary>
    private async Task<(string? Drift, string Physical)> TokensAsync(bool expectPhysicalToken = true)
    {
        CliRun preview = await RunAsync(["recover", "--release", CliHostFixture.ReleaseId, "--preview", "--json"]);
        JsonElement result = Envelope(preview).GetProperty("result");
        JsonElement drift = result.GetProperty("drift");
        string? driftToken = drift.GetProperty("reapprovalRequired").GetBoolean() ? drift.GetProperty("reapprovalToken").GetString() : null;
        if (!expectPhysicalToken)
        {
            return (driftToken, string.Empty);
        }

        return (driftToken, result.GetProperty("physicalReconciliation").GetProperty("reconciliationToken").GetString()!);
    }

    private Task<CliRun> ConfirmAsync(string? drift, string? physical)
    {
        List<string> args = ["recover", "--release", CliHostFixture.ReleaseId, "--confirm", CliHostFixture.ReleaseId, "--json"];
        if (drift is not null)
        {
            args.AddRange(["--reapprove-drift", drift]);
        }

        if (physical is not null)
        {
            args.AddRange(["--printers-reconciled", physical]);
        }

        return RunAsync([.. args]);
    }

    private Task<CliRun> RunAsync(string[] args) => RunAsync(args, _host.Configuration());

    private static async Task<CliRun> RunAsync(string[] args, IConfiguration configuration)
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
