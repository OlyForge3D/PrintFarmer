using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Update Manager Models
public class UpdateStatus
{
    [JsonPropertyName("busy")]
    public bool Busy { get; set; }

    [JsonPropertyName("github_rate_limit")]
    public int GithubRateLimit { get; set; }

    [JsonPropertyName("github_requests_remaining")]
    public int GithubRequestsRemaining { get; set; }

    [JsonPropertyName("github_limit_reset_time")]
    public long GithubLimitResetTime { get; set; }

    [JsonPropertyName("version_info")]
    public Dictionary<string, MoonrakerUpdateInfo> VersionInfo { get; set; } = new Dictionary<string, MoonrakerUpdateInfo>();
}

#pragma warning restore CA2227
