using System.Diagnostics;

namespace Farm.OrcaSlicer.Worker.Services;

public interface IOrcaBinaryDetector
{
    bool IsRealBinaryPresent();
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

            var fi = new FileInfo(BinaryPath);
            if (fi.Length < 2048)
            {
                return false; // heuristic stub threshold
            }

            using var proc = Process.Start(new ProcessStartInfo
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
}
