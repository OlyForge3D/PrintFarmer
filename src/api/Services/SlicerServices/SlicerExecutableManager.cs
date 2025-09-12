using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Farm.Web.Shared;
using Farm.Web.Api.Services.SlicerServices; // ensure symbol availability

namespace Farm.Web.Api.Services.SlicerServices;

/// <summary>
/// Reads slicer executable configuration from configuration and provides simple validation.
/// Configuration keys expected:
/// SlicerExecutables:{EngineName}:Path
/// SlicerExecutables:{EngineName}:ArgsTemplate  (optional, {input} and {output} placeholders recommended)
/// </summary>
public class SlicerExecutableManager : ISlicerExecutableManager
{
    private readonly IConfiguration _config;
    private readonly ILogger<SlicerExecutableManager> _logger;
    private readonly ISlicerSettingsService? _settingsService;

    public SlicerExecutableManager(IConfiguration config, ILogger<SlicerExecutableManager> logger, ISlicerSettingsService? settingsService = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsService = settingsService;
    }

    public bool TryGetExecutable(SlicerEngineType engine, out string? executablePath, out string? argsTemplate)
    {
        // Check runtime settings first (admin UI persisted values)
        if (_settingsService != null)
        {
            var runtime = _settingsService.GetSettings();
            if (runtime != null && runtime.PerEngine.TryGetValue(engine, out var eSetting) && !string.IsNullOrWhiteSpace(eSetting.Path))
            {
                executablePath = eSetting.Path;
                argsTemplate = eSetting.ArgsTemplate;
                return true;
            }
        }

        var section = _config.GetSection($"SlicerExecutables:{engine}");
        executablePath = section["Path"];
        argsTemplate = section["ArgsTemplate"];

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            // Try common names on PATH
            executablePath = engine switch
            {
                SlicerEngineType.PrusaSlicer => FindOnPath("prusa-slicer", "prusa-slicer.exe"),
                SlicerEngineType.OrcaSlicer => FindOnPath("orcaslicer", "orcaslicer.exe"),
                SlicerEngineType.SuperSlicer => FindOnPath("superslicer", "superslicer.exe"),
                SlicerEngineType.Cura => FindOnPath("cura", "cura.exe"),
                _ => null
            };
        }

        return !string.IsNullOrWhiteSpace(executablePath);
    }

    public async Task<bool> ValidateSlicerInstallationAsync(SlicerEngineType engine, CancellationToken cancellationToken = default)
    {
        if (!TryGetExecutable(engine, out var exe, out var _))
        {
            _logger.LogWarning("No configured executable found for slicer engine {Engine}", engine);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return false;
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate slicer executable at {Exe}", exe);
            return false;
        }
    }

    private static string? FindOnPath(string linuxName, string windowsName)
    {
        var name = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? windowsName : linuxName;
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var p in paths)
        {
            try
            {
                var candidate = Path.Combine(p, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }
}
