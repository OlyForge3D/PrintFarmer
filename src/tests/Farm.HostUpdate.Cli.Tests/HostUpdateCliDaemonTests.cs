using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>Issue #3114: the <c>daemon</c> command hosts the daemon service core with execution disabled.</summary>
public sealed class HostUpdateCliDaemonTests : IDisposable
{
    private readonly CliHostFixture _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(new[] { "daemon", "--release", "stable:1.2.3" }, "unknown_option:--release")]
    [InlineData(new[] { "daemon", "--once", "--once" }, "duplicate_option:--once")]
    [InlineData(new[] { "status", "--once" }, "unknown_option:--once")]
    public async Task Invalid_daemon_arguments_exit_with_usage(string[] args, string expectedError)
    {
        IReadOnlyDictionary<string, string> before = _host.Snapshot();

        CliRun run = await RunAsync(args, _host.Configuration());

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        run.Error.Should().Contain(expectedError);
        _host.Snapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Once_runs_a_single_disabled_cycle_and_emits_redacted_status_lines()
    {
        CliRun run = await RunAsync(["daemon", "--once", "--json"], _host.Configuration());

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, run.Output + run.Error);
        List<JsonElement> lines = Lines(run);
        lines.Should().NotBeEmpty();
        lines.Should().OnlyContain(line =>
            !line.GetProperty("executionEnabled").GetBoolean() &&
            line.GetProperty("executionGateCode").GetString() == DisabledHostUpdateDaemonExecutionGate.DisabledCode &&
            !line.GetProperty("enrolled").GetBoolean());
        lines.Should().Contain(line => line.GetProperty("identityStorage").GetString() == "NotConfigured");
        run.Output.Should().NotContain(_host.Root.Replace("\\", "\\\\")).And.NotContain(_host.Root);
    }

    [Fact]
    public async Task Once_text_output_names_the_disabled_gate()
    {
        CliRun run = await RunAsync(["daemon", "--once"], _host.Configuration());

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.Success, run.Output + run.Error);
        run.Output.Should().Contain("gate=" + DisabledHostUpdateDaemonExecutionGate.DisabledCode);
    }

    [Theory]
    [InlineData("HostUpdateDaemon:Enabled", "true", "daemon_setting_unknown:Enabled")]
    [InlineData("HostUpdateDaemon:PollIntervalSeconds", "5", "daemon_poll_interval_out_of_range")]
    [InlineData("HostUpdateDaemon:IdentityDirectory", "relative/identity", "daemon_identity_directory_not_absolute")]
    public async Task Invalid_daemon_configuration_is_configuration_unproven(string key, string value, string expected)
    {
        CliRun run = await RunAsync(["daemon", "--once", "--json"], _host.Configuration(v => v[key] = value));

        run.ExitCode.Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        run.Output.Should().Contain("configuration_invalid").And.Contain(expected);
    }

    private static List<JsonElement> Lines(CliRun run) =>
        run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();

    private static async Task<CliRun> RunAsync(string[] args, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(args, configuration, output, error, CancellationToken.None);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CliRun(int ExitCode, string Output, string Error);
}
