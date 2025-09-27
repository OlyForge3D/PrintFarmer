using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Settings
{
    public class AppSettings
    {
        // Add all settings properties here
        public NetworkDiscoverySettingsDto NetworkDiscovery { get; set; } = new();
        public SystemLogSettingsDto SystemLog { get; set; } = new();
        // Add other settings sections as needed
    }

    public class NetworkDiscoverySettingsDto
    {
        public bool EnableDiscovery { get; set; } = true;
        public string DiscoverySubnet { get; set; } = "10.0.0.0/24";
        // Add other network discovery settings
    }

    public class SystemLogSettingsDto
    {
        public int RetentionDays { get; set; } = 30;
        public bool EnableExport { get; set; } = true;
        // Add other log settings
    }
}
