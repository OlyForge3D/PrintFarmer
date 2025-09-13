using System.Diagnostics;

namespace Farm.PrusaSlicer.Worker.Services;

public interface IPrusaBinaryDetector
{
    bool IsRealBinaryPresent();
}

public sealed class PrusaBinaryDetector : IPrusaBinaryDetector
{
    private const string BinaryPath = "/usr/local/bin/prusa-slicer";
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
                { proc.Kill(); }
                catch { }
                return false;
            }
            return proc.ExitCode == 0 || proc.ExitCode == 1;
        }
        catch { return false; }
    }
}
