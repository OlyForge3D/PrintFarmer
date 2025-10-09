using System.Diagnostics;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Shared;
using InfraSlicerEngine = Farm.Infrastructure.Settings.SlicerEngineType;
using SharedSlicerEngine = Farm.Web.Shared.SlicerEngineType;

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Reads slicer executable configuration from configuration and provides simple validation.
/// Configuration keys expected:
/// SlicerExecutables:{EngineName}:Path
/// SlicerExecutables:{EngineName}:ArgsTemplate  (optional, {input} and {output} placeholders recommended)
/// </summary>
public class SlicerExecutableManager(IConfiguration config, IUnifiedLoggingService logger) : ISlicerExecutableManager
{
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    // Remove obsolete appSettingsService

    public bool TryGetExecutable(SharedSlicerEngine engine, out string? executablePath, out string? argsTemplate)
    {
        // Use SettingsService for settings access if needed

        IConfigurationSection section = _config.GetSection($"SlicerExecutables:{engine}");
        executablePath = section["Path"];
        argsTemplate = section["ArgsTemplate"];

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            // Try common names on PATH
            executablePath = engine switch
            {
                SharedSlicerEngine.PrusaSlicer => FindOnPath("prusa-slicer", "prusa-slicer.exe"),
                SharedSlicerEngine.OrcaSlicer => FindOnPath("orcaslicer", "orcaslicer.exe"),
                SharedSlicerEngine.SuperSlicer => FindOnPath("superslicer", "superslicer.exe"),
                SharedSlicerEngine.Cura => FindOnPath("cura", "cura.exe"),
                _ => null
            };
        }

        return !string.IsNullOrWhiteSpace(executablePath);
    }

    public async Task<bool> ValidateSlicerInstallationAsync(SharedSlicerEngine engine, CancellationToken cancellationToken = default)
    {
        if (!TryGetExecutable(engine, out string? exe, out string? _))
        {
            _logger.LogWarning($"No configured executable found for slicer engine {engine}");
            return false;
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to validate slicer executable at {exe}: {ex.Message}");
            return false;
        }
    }

    private static string? FindOnPath(string linuxName, string windowsName)
    {
        string name = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? windowsName : linuxName;
        string[] paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (string p in paths)
        {
            try
            {
                string candidate = Path.Combine(p, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch { }
        }
        return null;
    }
}
