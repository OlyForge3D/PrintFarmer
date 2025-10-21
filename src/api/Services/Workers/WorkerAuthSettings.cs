using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Configuration for worker API key authentication.
/// Single shared key is sufficient for initial rollout; future enhancement may introduce per-worker keys.
/// </summary>
public sealed class WorkerAuthSettings
{
    public const string SectionName = "WorkerAuth";

    /// <summary>
    /// Shared API key value that workers must present via X-Worker-Key header.
    /// If null/empty, endpoints fall back to permissive behavior only in Testing environment.
    /// </summary>
    [MaxLength(256)]
    public string? SharedKey { get; set; }
}
