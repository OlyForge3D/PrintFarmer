using Microsoft.Extensions.Configuration;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Builds the worker's advertised capability set for job routing (issue #578).
/// Emits both the generic <c>orcaslicer</c> tag (so legacy/unpinned jobs still
/// route to any worker) and the version-specific <c>orcaslicer:&lt;v&gt;</c>
/// tag (so version-pinned jobs are only claimed by the matching engine).
/// </summary>
public sealed class WorkerCapabilityProvider(IConfiguration configuration)
{
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <summary>
    /// The engine version this worker serves. Falls back to <c>SlicerRegistry:Version</c>
    /// or a sentinel <c>unknown</c> string (which the worker will refuse to accept
    /// version-pinned jobs for).
    /// </summary>
    public string EngineVersion
    {
        get
        {
            string? v = _configuration["Worker:EngineVersion"]
                     ?? _configuration["SlicerRegistry:Version"];
            return string.IsNullOrWhiteSpace(v) ? "unknown" : v.Trim();
        }
    }

    /// <summary>
    /// Full capability list advertised to server. Ordering does not matter —
    /// server matches via <c>ClaimNextJobAsync</c>'s OR-any semantics.
    /// </summary>
    public string[] GetCapabilities()
    {
        return
        [
            "orcaslicer",
            $"orcaslicer:{EngineVersion}",
            "stl-processing",
            "gcode-generation"
        ];
    }
}
