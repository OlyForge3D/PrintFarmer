using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// Issue #3119: restarting the hosted daemon service while a release is staged, executing, or
/// recovering resumes from the journaled checkpoint without replaying side effects, and every
/// status line stays redacted with execution disabled.
/// </summary>
public sealed class HostUpdateDaemonServiceRestartTests : IDisposable
{
    private readonly CliHostFixture _host = new();

    public void Dispose() => _host.Dispose();

    public static TheoryData<string, string, string, string> Interruptions => new()
    {
        // staging: nothing irreversible has happened, so the checkpoint waits for confirmation.
        { nameof(HostUpdateExecutionState.Preflight), "preflight:before", "confirmation_required", "confirmation_required" },
        // execution: the first boot records the interruption once; later boots only report it.
        { nameof(HostUpdateExecutionState.Applying), "apply:before", "execution_interrupted", "recovery_required" },
        // recovery: an interrupted host-local recovery is reported, never re-run by the daemon.
        { nameof(HostUpdateExecutionState.RecoveryRequired), "recovery:started", "recovery_interrupted", "recovery_interrupted" },
    };

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task Service_restart_resumes_from_the_journaled_checkpoint_without_replay(
        string state, string phase, string firstCode, string restartCode)
    {
        _host.SeedJournal(CliHostFixture.Request(), [(Enum.Parse<HostUpdateExecutionState>(state), phase)], withBaseline: false);
        int seeded = JournalLines();

        List<JsonElement> first = await RunServiceUntilCycleAsync();
        string afterFirst = File.ReadAllText(_host.JournalPath);
        int appended = JournalLines() - seeded;

        Checkpoint(first).Should().Be(firstCode);
        appended.Should().Be(firstCode == "execution_interrupted" ? 1 : 0, "a restart journals an interrupted execution's stop exactly once and nothing else");

        for (int restart = 0; restart < 2; restart++)
        {
            List<JsonElement> again = await RunServiceUntilCycleAsync();
            Checkpoint(again).Should().Be(restartCode);
            File.ReadAllText(_host.JournalPath).Should().Be(afterFirst, "subsequent restarts append nothing");
        }
    }

    private int JournalLines() =>
        File.ReadAllLines(_host.JournalPath).Count(line => !string.IsNullOrWhiteSpace(line));

    private static string? Checkpoint(List<JsonElement> lines) =>
        lines.Last(line => line.TryGetProperty("checkpoint", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
            .GetProperty("checkpoint").GetProperty("code").GetString();

    private async Task<List<JsonElement>> RunServiceUntilCycleAsync()
    {
        using var output = new CycleSignalingWriter();
        using var error = new StringWriter();
        using var log = new StringWriter();
        IConfiguration configuration = _host.Configuration();
        int? selfExit = null;
        using var host = new HostUpdateDaemonServiceHost(
            async token => await HostUpdateCli.RunAsync(["daemon", "--json"], configuration, output, error, token),
            log,
            code => selfExit = code);

        host.Start();
        await output.CycleCompleted.WaitAsync(TimeSpan.FromSeconds(30));
        host.Stop(TimeSpan.FromSeconds(30)).Should().Be(HostUpdateCliExitCodes.Success, output + error.ToString());
        selfExit.Should().BeNull("the supervisor, not the daemon, ended the service");

        string text = output.ToString();
        text.Should().NotContain(_host.Root).And.NotContain(_host.Root.Replace("\\", "\\\\"));
        List<JsonElement> lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();
        lines.Should().NotBeEmpty();
        lines.Should().OnlyContain(line =>
            !line.GetProperty("executionEnabled").GetBoolean() &&
            line.GetProperty("executionGateCode").GetString() == DisabledHostUpdateDaemonExecutionGate.DisabledCode);
        return lines;
    }

    private sealed class CycleSignalingWriter : StringWriter
    {
        private readonly TaskCompletionSource _cycle = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CycleCompleted => _cycle.Task;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null && value.Contains("daemon_cycle_completed", StringComparison.Ordinal))
            {
                _cycle.TrySetResult();
            }
        }
    }
}
