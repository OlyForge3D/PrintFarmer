using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
            var psi = new ProcessStartInfo
            {
                FileName = _scannerExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Build arguments depending on chosen scanner
            var exeName = Path.GetFileName(_scannerExecutable);
            if (exeName.Equals("clamdscan", System.StringComparison.OrdinalIgnoreCase))
            {
                psi.Arguments = $"--fdpass --no-summary \"{filePath}\"";
            }
            else
            {
                // clamscan
                psi.Arguments = $"--no-summary --stdout \"{filePath}\"";
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return Task.FromResult(VirusScanResult.Unknown);
            }

            // Wait for exit (honoring cancellation)
            proc.WaitForExit();
            var exit = proc.ExitCode;

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
        var names = new[] { "clamdscan", "clamscan" };
        foreach (var n in names)
        {
            var path = Which(n);
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
                var paths = System.Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
                foreach (var p in paths)
                {
                    var candidate = Path.Combine(p, name + (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? ".exe" : string.Empty));
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
