using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingDisplay(Name = "Network Discovery", Description = "Scan your network for printers.", Icon = "pf-icon-network", Group = "Networking", Order = 1)]
public class NetworkDiscoverySettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "NetworkDiscovery";

    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Enable Discovery", Description = "Enable or disable network printer discovery.", InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enableDiscovery")]
    public bool EnableDiscovery { get; set; } = true;

    /// <summary>
    /// List of subnets to scan, e.g. ["10.0.0.0/24", "192.168.1.0/24"]
    /// </summary>
    private static readonly string[] DefaultSubnets = new[] { "10.0.0.0/24", "10.0.5.0/24" };

    [SettingDisplay(Name = "Discovery Subnets", Description = "List of subnets to scan (CIDR notation).", InputType = SettingInputType.Array, IsMulti = true)]
    [JsonPropertyName("discoverySubnets")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Exposing as IList for serialization and API stability")]
    public IList<string> DiscoverySubnets { get; set; } = new List<string>(DefaultSubnets);

    [SettingDisplay(Name = "Client Timeout (ms)", Description = "Timeout for each network scan request in milliseconds.", InputType = SettingInputType.Number, MinValue = 50, MaxValue = 60000)]
    [JsonPropertyName("clientTimeoutMs")]
    public int ClientTimeoutMs { get; set; } = 200; // Valid range: 50-60000

    [SettingDisplay(Name = "Request Delay (ms)", MinValue = 0, MaxValue = 10000, Description = "Delay between network scan requests in milliseconds.", InputType = SettingInputType.Number)]
    [JsonPropertyName("requestDelayMs")]
    public int RequestDelayMs { get; set; } = 100; // Valid range: 0-10000

    [SettingDisplay(Name = "Max Concurrent Requests", MinValue = 1, MaxValue = 100, Description = "Maximum number of concurrent network scan requests.", InputType = SettingInputType.Number)]
    [JsonPropertyName("maxConcurrentRequests")]
    public int MaxConcurrentRequests { get; set; } = 20; // Valid range: 1-100

    [SettingDisplay(Name = "Max Retries", MinValue = 0, MaxValue = 10, Description = "Maximum number of retry attempts for failed requests.", InputType = SettingInputType.Number)]
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 2; // Valid range: 0-10

    /// <summary>
    /// Enable or disable the background periodic discovery service.
    /// When enabled, the system will automatically scan for new printers at the configured interval.
    /// </summary>
    [SettingDisplay(Name = "Enable Background Scanning", Description = "Automatically scan for new printers in the background.", InputType = SettingInputType.Boolean)]
    [JsonPropertyName("backgroundScanEnabled")]
    public bool BackgroundScanEnabled { get; set; } = false;

    /// <summary>
    /// Interval between background discovery scans in minutes.
    /// </summary>
    [SettingDisplay(Name = "Scan Interval (minutes)", MinValue = 1, MaxValue = 1440, Description = "How often to scan for new printers (in minutes).", InputType = SettingInputType.Number)]
    [JsonPropertyName("backgroundScanIntervalMinutes")]
    public int BackgroundScanIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// UTC timestamp of the last heartbeat from the discovery service.
    /// Used to determine if the discovery service is actively running.
    /// </summary>
    [JsonPropertyName("lastHeartbeat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastHeartbeat { get; set; }

    public void Validate()
    {
        EnsureUniqueSubnets();
        if (DiscoverySubnets == null || DiscoverySubnets.Count == 0 || DiscoverySubnets.Any(string.IsNullOrWhiteSpace))
        {
            throw new ValidationException("At least one valid subnet is required.");
        }

        foreach (string subnet in DiscoverySubnets)
        {
            if (!IsValidCidr(subnet))
            {
                throw new ValidationException($"Invalid CIDR subnet: {subnet}");
            }
        }
    }

    private void EnsureUniqueSubnets()
    {
        if (DiscoverySubnets == null)
        {
            return;
        }

        List<string> unique = DiscoverySubnets.Distinct().ToList();
        if (unique.Count != DiscoverySubnets.Count)
        {
            DiscoverySubnets.Clear();
            foreach (string? subnet in unique)
            {
                DiscoverySubnets.Add(subnet);
            }
        }
    }

    // Basic CIDR validation (IPv4 only)
    private static bool IsValidCidr(string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        string[] parts = cidr.Split('/');
        return parts.Length != 2
            ? false
            : !IPAddress.TryParse(parts[0], out _) ? false : int.TryParse(parts[1], out int prefix) && prefix >= 0 && prefix <= 32;
    }
}
