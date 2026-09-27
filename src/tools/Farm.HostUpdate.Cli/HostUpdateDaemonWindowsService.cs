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

    private readonly HostUpdateDaemonLogWriter _log;
    private readonly HostUpdateDaemonServiceHost _host;
    private volatile bool _started;

    private HostUpdateDaemonWindowsService(IReadOnlyList<string> args, Func<IConfiguration> configurationFactory, string logPath)
    {
        _log = new HostUpdateDaemonLogWriter(logPath);
        _host = new HostUpdateDaemonServiceHost(
            token => HostUpdateCli.RunAsync(args, configurationFactory, _log, _log, token),
            _log,
            OnDaemonExited);
        ServiceName = WindowsServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        AutoLog = false;
    }

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PrintFarmer", "host", "daemon", "logs", "daemon.log");

    public static int Run(IReadOnlyList<string> args, Func<IConfiguration> configurationFactory, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configurationFactory);
        ArgumentNullException.ThrowIfNull(error);
        using var service = new HostUpdateDaemonWindowsService(args, configurationFactory, DefaultLogPath);
        Run(service);
        if (!service._started)
        {
            // ServiceBase.Run returns without starting when not launched by the SCM.
            error.WriteLine("windows_service_not_started: --windows-service must be started by the Service Control Manager");
            return HostUpdateDaemonServiceHost.ServiceFailed;
        }

        return service.ExitCode;
    }

    protected override void OnStart(string[] args)
    {
        _started = true;
        _host.Start();
    }

    protected override void OnStop() => StopDaemon();

    protected override void OnShutdown() => StopDaemon();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _host.Dispose();
            _log.Dispose();
        }

        base.Dispose(disposing);
    }

    private void StopDaemon() => ExitCode = _host.Stop(HostUpdateDaemonServiceHost.DefaultStopWait);

    // The daemon ended on its own (configuration invalid, lock held, ...): report its exit code to
    // the Service Control Manager so the failure is visible and the recovery policy applies.
    private void OnDaemonExited(int exitCode)
    {
        ExitCode = exitCode;
        Stop();
    }
}

/// <summary>
/// Platform-neutral lifecycle of the service-hosted daemon: one run, cancelled on stop, with a
/// bounded stop wait and a callback when the daemon exits on its own.
/// </summary>
internal sealed class HostUpdateDaemonServiceHost : IDisposable
{
    /// <summary>Exit code reported when the daemon failed unexpectedly or did not stop in time.</summary>
    public const int ServiceFailed = 1;

    public static readonly TimeSpan DefaultStopWait = TimeSpan.FromSeconds(25);

    private readonly Func<CancellationToken, Task<int>> _run;
#pragma warning disable CA2213 // The log is owned and disposed by the caller.
    private readonly TextWriter _log;
#pragma warning restore CA2213
    private readonly Action<int> _onSelfExit;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _exited = new();
    private int _state;
    private volatile bool _stopping;
    private int _exitCode = HostUpdateDaemonServiceHost.ServiceFailed;

    public HostUpdateDaemonServiceHost(Func<CancellationToken, Task<int>> run, TextWriter log, Action<int> onSelfExit)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(onSelfExit);
        _run = run;
        _log = log;
        _onSelfExit = onSelfExit;
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("The daemon has already been started.");
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Cancels the daemon and waits up to <paramref name="wait"/>. Returns the daemon's exit code,
    /// or <see cref="HostUpdateDaemonServiceHost.ServiceFailed"/> when it did not stop in time.
    /// </summary>
    public int Stop(TimeSpan wait)
    {
        _stopping = true;
        _cancellation.Cancel();
        if (Volatile.Read(ref _state) == 0)
        {
            return HostUpdateCliExitCodes.Success;
        }

        return _exited.Wait(wait) ? _exitCode : HostUpdateDaemonServiceHost.ServiceFailed;
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _exited.Dispose();
    }

    private async Task RunAsync()
    {
        int exitCode;
        try
        {
            exitCode = await _run(_cancellation.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The service must report any daemon failure to the SCM rather than crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            await _log.WriteLineAsync("daemon: stopped unexpectedly").ConfigureAwait(false);
            exitCode = HostUpdateDaemonServiceHost.ServiceFailed;
        }

        _exitCode = exitCode;
        await _log.FlushAsync().ConfigureAwait(false);
        _exited.Set();
        if (!_stopping)
        {
            _onSelfExit(exitCode);
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
