using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Singleton entity storing the Home Assistant integration configuration.
/// One row in the database — use Id = 1 by convention.
/// </summary>
public class HomeAssistantSettings
{
    /// <summary>Primary key. Always 1 (singleton pattern).</summary>
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>Whether the Home Assistant integration is active.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL of the Home Assistant instance (e.g. http://homeassistant.local:8123).</summary>
    [MaxLength(500)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Long-lived access token for authenticating with the HA REST API.
    /// Encrypted at rest via <see cref="Farm.Infrastructure.Services.Security.ISensitiveDataProtector"/>.
    /// </summary>
    [MaxLength(2000)]
    public string? LongLivedAccessToken { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
