using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Hosts <c>daemon</c> under the Windows Service Control Manager (issue #3118). The service is only
/// a supervisor: it runs exactly the console daemon, whose gate stays disabled, so installing or
/// starting it authorizes nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HostUpdateDaemonWindowsService : ServiceBase
{
    public const string WindowsServiceName = "PrintFarmerHostUpdateDaemon";

    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(25);

    private readonly IReadOnlyList<string> _args;
    private readonly Func<IConfiguration> _configurationFactory;
    private readonly HostUpdateDaemonLogWriter _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _exited = new();
    private volatile bool _stopping;
    private int _daemonExitCode = 1;

    private HostUpdateDaemonWindowsService(IReadOnlyList<string> args, Func<IConfiguration> configurationFactory, string logPath)
    {
        _args = args;
        _configurationFactory = configurationFactory;
        _log = new HostUpdateDaemonLogWriter(logPath);
        ServiceName = WindowsServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        AutoLog = false;
    }

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PrintFarmer", "host", "daemon", "logs", "daemon.log");

    public static int Run(IReadOnlyList<string> args, Func<IConfiguration> configurationFactory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configurationFactory);
        using var service = new HostUpdateDaemonWindowsService(args, configurationFactory, DefaultLogPath);
        Run(service);
        return service.ExitCode;
    }

    protected override void OnStart(string[] args) => _ = Task.Run(RunDaemonAsync);

    protected override void OnStop() => StopDaemon();

    protected override void OnShutdown() => StopDaemon();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation.Dispose();
            _exited.Dispose();
            _log.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RunDaemonAsync()
    {
        int exitCode;
        try
        {
            exitCode = await HostUpdateCli.RunAsync(_args, _configurationFactory, _log, _log, _cancellation.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The service must report any daemon failure to the SCM rather than crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            await _log.WriteLineAsync("daemon: stopped unexpectedly").ConfigureAwait(false);
            exitCode = 1;
        }

        _daemonExitCode = exitCode;
        await _log.FlushAsync().ConfigureAwait(false);
        _exited.Set();

        // The daemon ended on its own (configuration invalid, lock held, ...): report its exit code
        // to the Service Control Manager so the failure is visible and the recovery policy applies.
        if (!_stopping)
        {
            ExitCode = exitCode;
            Stop();
        }
    }

    private void StopDaemon()
    {
        _stopping = true;
        _cancellation.Cancel();
        if (_exited.Wait(StopWait))
        {
            ExitCode = _daemonExitCode;
        }
    }
}

/// <summary>
/// Line-buffered, append-only log for the Windows service's redacted daemon status lines. It
/// rotates once to <c>daemon.log.1</c> so the log stays bounded.
/// </summary>
internal sealed class HostUpdateDaemonLogWriter : TextWriter
{
    public const long DefaultMaxBytes = 1024 * 1024;

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly StringBuilder _line = new();
    private readonly object _gate = new();

    public HostUpdateDaemonLogWriter(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _path = path;
        _maxBytes = maxBytes;
    }

    public override Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                FlushLine();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (char c in value)
            {
                Write(c);
            }
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            if (_line.Length > 0)
            {
                FlushLine();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    private void FlushLine()
    {
        string entry = $"{DateTimeOffset.UtcNow:O} {_line}\n";
        _line.Clear();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var existing = new FileInfo(_path);
            if (existing.Exists && existing.Length + Encoding.GetByteCount(entry) > _maxBytes)
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }

            File.AppendAllText(_path, entry, Encoding);
        }
        catch (IOException)
        {
            // A log that cannot be written must never stop the daemon.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
