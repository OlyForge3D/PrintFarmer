using FluentAssertions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>Issue #3118: the opt-in Windows service hosts the same disabled daemon.</summary>
public sealed class HostUpdateDaemonWindowsServiceTests : IDisposable
{
    private readonly CliHostFixture _host = new();
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pf-daemon-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Windows_service_flag_is_accepted_only_for_the_daemon_on_windows()
    {
        (int exitCode, string error) = await RunAsync(["daemon", "--windows-service", "--once"]);

        exitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        error.Should().Contain(OperatingSystem.IsWindows()
            ? "windows_service_conflicts_with:--once"
            : "unknown_option:--windows-service");
    }

    [Fact]
    public async Task Windows_service_flag_is_rejected_for_other_commands()
    {
        (int exitCode, string error) = await RunAsync(["status", "--windows-service"]);

        exitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        error.Should().Contain("unknown_option:--windows-service");
    }

    [Fact]
    public void Log_writer_appends_timestamped_lines_only_when_complete()
    {
        string path = Path.Combine(_logDirectory, "daemon.log");
        using (var log = new HostUpdateDaemonLogWriter(path))
        {
            log.Write("daemon: ");
            File.Exists(path).Should().BeFalse();
            log.WriteLine("started\r");
            log.Write("partial");
        }

        string[] lines = File.ReadAllLines(path);
        lines.Should().HaveCount(2);
        lines[0].Should().EndWith(" daemon: started");
        lines[1].Should().EndWith(" partial");
        DateTimeOffset.TryParse(lines[0].Split(' ')[0], out _).Should().BeTrue();
    }

    [Fact]
    public void Log_writer_rotates_once_when_the_log_would_exceed_its_bound()
    {
        string path = Path.Combine(_logDirectory, "daemon.log");
        using var log = new HostUpdateDaemonLogWriter(path, maxBytes: 100);

        log.WriteLine("first " + new string('a', 40));
        log.WriteLine("second " + new string('b', 40));
        log.WriteLine("third " + new string('c', 40));

        File.ReadAllText(path).Should().Contain("third").And.NotContain("second");
        File.ReadAllText(path + ".1").Should().Contain("second").And.NotContain("first");
    }

    private async Task<(int ExitCode, string Error)> RunAsync(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(args, _host.Configuration(), output, error, CancellationToken.None);
        return (exitCode, error.ToString());
    }
}
