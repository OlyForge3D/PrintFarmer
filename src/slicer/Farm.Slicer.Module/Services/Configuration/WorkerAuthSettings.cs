using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Configuration for worker API key authentication.
/// Single shared key is sufficient for initial rollout; future enhancement may introduce per-worker keys.
/// </summary>
public sealed class WorkerAuthSettings
{
    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "WorkerAuth";

    /// <summary>
    /// Shared API key value that workers must present via X-Worker-Key header.
    /// The slicer host fails startup when this is empty unless the explicit
    /// development-only registration bypass is enabled.
    /// </summary>
    [MaxLength(256)]
    public string? SharedKey { get; set; }

    /// <summary>
    /// Allows unauthenticated slicer registration only when the host environment
    /// is Development. This unsafe option is rejected in every other environment.
    /// </summary>
    public bool AllowInsecureDevelopmentRegistration { get; set; }
}
