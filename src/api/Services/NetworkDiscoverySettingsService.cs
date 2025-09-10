using System.Text.Json;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

public class NetworkDiscoverySettingsService : INetworkDiscoverySettingsService
{
    private readonly ILogger<NetworkDiscoverySettingsService> _logger;
    private NetworkDiscoverySettingsDto? _settings;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "network.discovery.settings.json");
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };
    private static readonly char[] PortSeparators = [',', ';', ' '];

    public NetworkDiscoverySettingsService(ILogger<NetworkDiscoverySettingsService> logger)
    {
        _logger = logger;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<NetworkDiscoverySettingsDto>(json);
                if (cfg is not null)
                {
                    _settings = cfg;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load network discovery settings from {Path}", _path);
        }
    }

    public NetworkDiscoverySettingsDto GetSettings()
    {
        if (_settings != null && _settings.NetworkRanges.Count > 0)
        {
            return _settings;
        }

        // If no user settings exist, return empty settings - user must configure ranges
        // Derive default ports; allow override via environment variable DISCOVERY_PORTS (e.g. "80,7125,7912")
        var envPorts = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
        List<int> defaultPorts;
        if (!string.IsNullOrWhiteSpace(envPorts))
        {
            defaultPorts = [.. envPorts
                .Split(PortSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : -1)
                .Where(p => p > 0 && p < 65536)
                .Distinct()];
            if (defaultPorts.Count == 0)
            {
                defaultPorts = [7125, 80]; // Fallback if parsing failed
            }
        }
        else
        {
            // Default discovery ports: Moonraker (7125) and HTTP (80)
            defaultPorts = [7125, 80];
        }

        return new NetworkDiscoverySettingsDto(
            [], // Empty - user must specify network ranges
            100, // Default timeout: 100ms per host
            15,  // Default max concurrent scans
            defaultPorts // Default / environment-derived ports
        );
    }

    public void SaveSettings(NetworkDiscoverySettingsDto settings)
    {
        _settings = settings;
        try
        {
            var json = JsonSerializer.Serialize(_settings, s_writeOptions);
            File.WriteAllText(_path, json);
            _logger.LogInformation("Saved network discovery settings to {Path}", _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save network discovery settings to {Path}", _path);
        }
    }
}
