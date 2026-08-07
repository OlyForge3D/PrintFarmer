using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Farm.OrcaSlicer.Worker.Services;

public interface IOrcaBinaryDetector
{
    bool IsRealBinaryPresent();

    /// <summary>
    /// Get the installed OrcaSlicer version (e.g., "1.7.0" or "2.0.0").
    /// Returns null if version cannot be determined.
    /// </summary>
    Task<string?> GetVersionAsync();
}

public sealed class OrcaBinaryDetector : IOrcaBinaryDetector
{
    /// <summary>
    /// Default binary path used when <c>Worker:OrcaSlicerPath</c> is not
    /// configured. MUST match <see cref="OrcaSlicingPipelineService"/> so the
    /// startup gate verifies the SAME executable that jobs will invoke
    /// (issue #578 / Hicks R4 finding #4).
    /// </summary>
    internal const string DefaultBinaryPath = "/opt/orcaslicer/bin/orca-slicer";

    private readonly string _binaryPath;

    internal string BinaryPath => _binaryPath;

    public OrcaBinaryDetector(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _binaryPath = configuration["Worker:OrcaSlicerPath"] ?? DefaultBinaryPath;
    }

    public bool IsRealBinaryPresent()
    {
        try
        {
            if (!File.Exists(_binaryPath))
            {
                return false;
            }

            FileInfo fi = new FileInfo(_binaryPath);
            if (fi.Length < 2048 && !IsTrustedLauncher(_binaryPath))
            {
                return false;
            }

            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = _binaryPath,
                Arguments = "--help",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (proc == null)
            {
                return false;
            }

            if (!proc.WaitForExit(4000))
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                    // ignored
                }

                return false;
            }

            return proc.ExitCode == 0 || proc.ExitCode == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTrustedLauncher(string path)
    {
        return IsTrustedLauncher(path, "/usr/local/bin/orcaslicer", "/opt/orcaslicer/bin/orca-slicer");
    }

    internal static bool IsTrustedLauncher(string path, string launcherPath, string realBinaryPath)
    {
        if (!string.Equals(path, launcherPath, StringComparison.Ordinal))
        {
            return false;
        }

        FileInfo realBinary = new(realBinaryPath);
        if (!realBinary.Exists || realBinary.Length < 2048)
        {
            return false;
        }

        string launcher = File.ReadAllText(path);
        return Regex.IsMatch(
            launcher,
            @"(?m)^\s*exec\s+[""']?(?:""\$APPDIR""|\$APPDIR|\$\{APPDIR\})/bin/orca-slicer(?:[""']|\s|$)",
            RegexOptions.CultureInvariant);
    }

    public async Task<string?> GetVersionAsync()
    {
        try
        {
            if (!File.Exists(_binaryPath))
            {
                return null;
            }

            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = _binaryPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (proc == null)
            {
                return null;
            }

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            void OnExited(object? sender, EventArgs e)
            {
                _ = tcs.TrySetResult(true);
            }

            proc.EnableRaisingEvents = true;
            proc.Exited += OnExited;

            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(4000)).ConfigureAwait(false);
            if (completedTask != tcs.Task)
            {
                try
                {
                    proc.Kill();
                }
                catch
                {
                }

                return null;
            }

            string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            // Parse version from output: typically "OrcaSlicer 1.7.0" or similar
            // Extract the version number pattern (e.g., "1.7.0", "2.0.0")
            Match versionMatch = Regex.Match(output, @"(\d+\.\d+(?:\.\d+)?)");
            return versionMatch.Success ? versionMatch.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}
