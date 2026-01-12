using System.Diagnostics;
using System.Text.RegularExpressions;

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
    private const string BinaryPath = "/usr/local/bin/orcaslicer";

    public bool IsRealBinaryPresent()
    {
        try
        {
            if (!File.Exists(BinaryPath))
            {
                return false;
            }

            FileInfo fi = new FileInfo(BinaryPath);
            if (fi.Length < 2048)
            {
                return false; // heuristic stub threshold
            }

            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = BinaryPath,
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
        catch { return false; }
    }

    public async Task<string?> GetVersionAsync()
    {
        try
        {
            if (!File.Exists(BinaryPath))
            {
                return null;
            }

            using Process? proc = Process.Start(new ProcessStartInfo
            {
                FileName = BinaryPath,
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
                catch { }
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
