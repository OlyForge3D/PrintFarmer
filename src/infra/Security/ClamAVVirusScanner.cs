using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Farm.Infrastructure.Security;

/// <summary>
/// ClamAV-based virus scanner integration. Uses <c>clamscan</c> or <c>clamdscan</c> if available on PATH.
/// Falls back to <see cref="VirusScanResult.Unknown"/> when the scanner is not present or an error occurs.
/// NOTE: This implementation is best-effort; absence of ClamAV is not considered a hard failure for uploads.
/// </summary>
public class ClamAVVirusScanner : IVirusScanner
{
    private readonly string? _scannerExecutable;

    public ClamAVVirusScanner()
    {
        // Prefer clamdscan for performance where available
        _scannerExecutable = FindScannerExecutable();
    }

    public async Task<VirusScanResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_scannerExecutable) || !File.Exists(filePath))
        {
            return VirusScanResult.Unknown;
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
            psi.Arguments = exeName.Equals("clamdscan", StringComparison.OrdinalIgnoreCase)
                ? $"--fdpass --no-summary \"{filePath}\""
                : $"--no-summary --stdout \"{filePath}\""; // clamscan

            using Process? proc = Process.Start(psi);
            if (proc == null)
            {
                return VirusScanResult.Unknown;
            }

            // Apply a soft timeout (e.g., 30s) in case ClamAV hangs; cancellation token still honored.
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Swallow secondary kill exceptions
                }

                return VirusScanResult.Unknown;
            }

            int exit = proc.ExitCode;
            // clamscan/clamdscan: 0 = clean, 1 = infected, >1 = error
            return exit switch
            {
                0 => VirusScanResult.Clean,
                1 => VirusScanResult.Infected,
                _ => VirusScanResult.Unknown
            };
        }
        catch
        {
            return VirusScanResult.Unknown;
        }
    }

    private static string? FindScannerExecutable()
    {
        // Look for clamdscan first, then clamscan
        string[] names = ["clamdscan", "clamscan"]; // collection expression (.NET 8/9)
        foreach (string n in names)
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
                string[] paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
                foreach (string p in paths)
                {
                    string candidate = Path.Combine(p, name + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty));
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
