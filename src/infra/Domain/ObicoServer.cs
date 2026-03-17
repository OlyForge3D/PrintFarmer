using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents an Obico ML API server for AI-powered print failure detection.
/// Multiple servers can be configured to distribute load across GPU machines
/// or assign specific servers to specific printers.
/// </summary>
public class ObicoServer
{
    /// <summary>
    /// Unique identifier for this Obico server.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for this Obico server (e.g., "GPU Server 1", "Production ML").
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the Obico ML API server (e.g., "http://obico-ml-api:3333").
    /// Must be a valid HTTP or HTTPS URL.
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Optional API key or token for authenticating with this Obico server.
    /// Sent as a Bearer token in the Authorization header when present.
    /// Self-hosted Obico ML servers may not require authentication.
    /// </summary>
    [MaxLength(500)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Whether this server is currently enabled for failure detection.
    /// Disabled servers are skipped during analysis.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent image analyses this server can handle.
    /// Used for load balancing across multiple printers.
    /// </summary>
    public int MaxConcurrentAnalyses { get; set; } = 4;

    /// <summary>
    /// Timestamp when this server configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when this server configuration was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Collection of printers assigned to this Obico server.
    /// </summary>
    public ICollection<Printer> Printers { get; set; } = new List<Printer>();
}
