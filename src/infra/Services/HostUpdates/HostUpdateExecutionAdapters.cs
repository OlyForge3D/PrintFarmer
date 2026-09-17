using System.Diagnostics;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Result of running an external process without a shell, capturing exit code and output for
/// diagnostics. Never includes secrets in <see cref="StandardOutput"/>/<see cref="StandardError"/>
/// callers are expected to redact sensitive arguments before logging them.
/// </summary>
public sealed record HostUpdateProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a host-local executable using an explicit argument list (never a shell command line),
/// so adapters cannot be subverted by argument/shell interpolation.
/// </summary>
public interface IHostUpdateProcessRunner
{
    Task<HostUpdateProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>
/// Default <see cref="IHostUpdateProcessRunner"/> backed by <see cref="Process"/>. Arguments are
/// passed via <see cref="ProcessStartInfo.ArgumentList"/> so no shell parses them.
/// </summary>
public sealed class DefaultHostUpdateProcessRunner : IHostUpdateProcessRunner
{
    public async Task<HostUpdateProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Process '{fileName}' timed out after {timeout}.");
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);
        return new HostUpdateProcessResult(process.ExitCode, stdOut, stdErr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill attempt.
        }
    }
}
