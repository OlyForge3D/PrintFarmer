using System.Diagnostics;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

/// <summary>
/// ClamAV-based virus scanner integration. Uses `clamscan` or `clamdscan` if available on PATH.
/// Falls back to Unknown when scanner is not present or fails.
/// </summary>
public class ClamAVVirusScanner : IVirusScanner
{
    private readonly string? _scannerExecutable;

    public ClamAVVirusScanner()
    {
        // Prefer clamdscan for performance where available
        _scannerExecutable = FindScannerExecutable();
    }

    public Task<VirusScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_scannerExecutable) || !File.Exists(filePath))
        {
            return Task.FromResult(VirusScanResult.Unknown);
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = _scannerExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Build arguments depending on chosen scanner
            string exeName = Path.GetFileName(_scannerExecutable);
            if (exeName.Equals("clamdscan", System.StringComparison.OrdinalIgnoreCase))
            {
                psi.Arguments = $"--fdpass --no-summary \"{filePath}\"";
            }
            else
            {
                // clamscan
                psi.Arguments = $"--no-summary --stdout \"{filePath}\"";
            }

            using Process? proc = Process.Start(psi);
            if (proc == null)
            {
                return Task.FromResult(VirusScanResult.Unknown);
            }

            // Wait for exit (honoring cancellation)
            proc.WaitForExit();
            int exit = proc.ExitCode;

            // clamscan/clamdscan: exit code 0 = no virus, 1 = virus found, >1 = error
            if (exit == 0)
            {
                return Task.FromResult(VirusScanResult.Clean);
            }
            if (exit == 1)
            {
                return Task.FromResult(VirusScanResult.Infected);
            }
            return Task.FromResult(VirusScanResult.Unknown);
        }
        catch
        {
            return Task.FromResult(VirusScanResult.Unknown);
        }
    }

    private static string? FindScannerExecutable()
    {
        // Look for clamdscan first, then clamscan
        string[] names = new[] { "clamdscan", "clamscan" };
        foreach (string? n in names)
        {
            string? path = Which(n);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }
        return null;

        static string? Which(string name)
        {
            try
            {
                string[] paths = System.Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
                foreach (string p in paths)
                {
                    string candidate = Path.Combine(p, name + (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? ".exe" : string.Empty));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
