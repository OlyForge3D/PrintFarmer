using System.ComponentModel.DataAnnotations;

namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Configuration for bootstrapping slicer worker registration.
/// </summary>
public sealed class WorkerAuthSettings
{
    /// <summary>Configuration section name in appsettings.</summary>
    public const string SectionName = "WorkerAuth";

    /// <summary>
    /// Shared key used only to register a worker. Successful registration issues
    /// a distinct service identity and key for worker-only requests.
    /// </summary>
    [MaxLength(256)]
    public string? SharedKey { get; set; }
}
