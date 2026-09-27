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

    [Fact]
    public void Log_writer_counts_lines_it_cannot_write_and_reports_them_later()
    {
        // A directory at the log path makes every append fail with an I/O error.
        string path = Path.Combine(_logDirectory, "daemon.log");
        Directory.CreateDirectory(path);
        using var log = new HostUpdateDaemonLogWriter(path);

        log.WriteLine("lost one");
        log.WriteLine("lost two");
        log.DroppedLines.Should().Be(2);

        Directory.Delete(path);
        log.WriteLine("kept");

        log.DroppedLines.Should().Be(0);
        string[] lines = File.ReadAllLines(path);
        lines.Should().HaveCount(2);
        lines[0].Should().EndWith(" log: 2 earlier line(s) could not be written");
        lines[1].Should().EndWith(" kept");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\data")]
    [InlineData("C:relative")]
    public void Log_path_fails_closed_when_program_data_is_not_absolute(string? commonApplicationData)
    {
        HostUpdateDaemonLogWriter.ResolveServiceLogPath(commonApplicationData).Should().BeNull();
    }

    [Fact]
    public void Log_path_is_fixed_under_program_data()
    {
        string root = Path.GetFullPath(Path.GetTempPath());

        HostUpdateDaemonLogWriter.ResolveServiceLogPath(root).Should()
            .Be(Path.Join(root, "PrintFarmer", "host", "daemon", "logs", "daemon.log"));
    }

    [Fact]
    public async Task Service_host_reports_a_self_exit_code_to_the_supervisor()
    {
        using var log = new StringWriter();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostUpdateDaemonServiceHost(_ => Task.FromResult(HostUpdateCliExitCodes.ConfigurationUnproven), log, exited.SetResult);

        host.Start();

        (await exited.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
        host.Stop(TimeSpan.FromSeconds(1)).Should().Be(HostUpdateCliExitCodes.ConfigurationUnproven);
    }

    [Fact]
    public async Task Service_host_stop_cancels_the_daemon_and_returns_its_exit_code_without_a_self_exit()
    {
        using var log = new StringWriter();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool selfExit = false;
        using var host = new HostUpdateDaemonServiceHost(
            async token =>
            {
                running.SetResult();
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                return HostUpdateCliExitCodes.Success;
            },
            log,
            _ => selfExit = true);

        host.Start();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        host.Stop(TimeSpan.FromSeconds(10)).Should().Be(HostUpdateCliExitCodes.Success);
        selfExit.Should().BeFalse();
    }

    [Fact]
    public async Task Service_host_stop_reports_failure_when_the_daemon_does_not_stop_in_time()
    {
        using var log = new StringWriter();
        using var release = new ManualResetEventSlim();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostUpdateDaemonServiceHost(
            _ =>
            {
                running.SetResult();
                release.Wait(TimeSpan.FromSeconds(30));
                return Task.FromResult(HostUpdateCliExitCodes.Success);
            },
            log,
            _ => { });

        host.Start();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        host.Stop(TimeSpan.FromMilliseconds(50)).Should().Be(HostUpdateDaemonServiceHost.ServiceFailed);
        release.Set();
    }

    [Fact]
    public async Task Service_host_reports_an_unexpected_daemon_failure_without_its_details()
    {
        using var log = new StringWriter();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostUpdateDaemonServiceHost(
            _ => throw new InvalidOperationException("secret-path"), log, exited.SetResult);

        host.Start();

        (await exited.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(HostUpdateDaemonServiceHost.ServiceFailed);
        log.ToString().Should().Contain("daemon: stopped unexpectedly").And.NotContain("secret-path");
        FluentActions.Invoking(host.Start).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Service_host_stop_before_start_is_a_clean_stop()
    {
        using var log = new StringWriter();
        using var host = new HostUpdateDaemonServiceHost(_ => Task.FromResult(HostUpdateCliExitCodes.Success), log, _ => { });

        host.Stop(TimeSpan.FromSeconds(1)).Should().Be(HostUpdateCliExitCodes.Success);
    }

    [Theory]
    [InlineData("daemon", "--windows-service", "--once")]
    [InlineData("daemon", "--windows-service", "--windows-service")]
    [InlineData("daemon", "--windows-service", "--unknown")]
    public async Task Executable_validates_windows_service_forms_before_service_dispatch(params string[] args)
    {
        (int exitCode, string error) = await RunProcessAsync(args);

        exitCode.Should().Be(HostUpdateCliExitCodes.Usage);
        error.Should().Contain("Usage:");
    }

    [Fact]
    public async Task Executable_windows_service_outside_the_service_control_manager_fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (int exitCode, string error) = await RunProcessAsync("daemon", "--windows-service");

        exitCode.Should().Be(HostUpdateDaemonServiceHost.ServiceFailed);
        error.Should().Contain("windows_service_not_started");
    }

    private static async Task<(int ExitCode, string Error)> RunProcessAsync(params string[] args)
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

        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("HostUpdate", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        _ = await stdout;
        return (process.ExitCode, await stderr);
    }

    private async Task<(int ExitCode, string Error)> RunAsync(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await HostUpdateCli.RunAsync(args, _host.Configuration(), output, error, CancellationToken.None);
        return (exitCode, error.ToString());
    }
}
