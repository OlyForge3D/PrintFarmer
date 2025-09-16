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
        // Derive default ports; allow override via environment variable DISCOVERY_PORTS (e.g. "80,7912")
        var envPorts = Environment.GetEnvironmentVariable("DISCOVERY_PORTS");
        List<int> defaultPorts;
        if (!string.IsNullOrWhiteSpace(envPorts))
        {
            defaultPorts = envPorts
                .Split(PortSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var p) ? p : -1)
                .Where(p => p > 0 && p < 65536)
                .Distinct()
                .ToList();
            if (defaultPorts.Count == 0)
            {
                defaultPorts = new List<int> { 80 }; // Fallback if parsing failed
            }
        }
        else
        {
            // Default discovery ports: HTTP (80)
            defaultPorts = new List<int> { 80 };
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
        ArgumentNullException.ThrowIfNull(settings);

        // Validate settings before saving
        var validation = NetworkValidationService.ValidateSettings(settings);

        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            _logger.LogWarning("Network discovery settings validation failed: {Errors}", errors);
            throw new ArgumentException($"Invalid network discovery settings: {errors}");
        }

        // Log warnings if any
        if (validation.Warnings.Count > 0)
        {
            var warnings = string.Join("; ", validation.Warnings);
            _logger.LogWarning("Network discovery settings saved with warnings: {Warnings}", warnings);
        }

        _settings = settings;
        try
        {
            var json = JsonSerializer.Serialize(_settings, s_writeOptions);
            File.WriteAllText(_path, json);
            _logger.LogInformation("Saved network discovery settings to {Path} - {RangeCount} ranges, {PortCount} ports", _path, settings.NetworkRanges.Count, settings.Ports.Count);

            // Log suggestions if any
            if (validation.Suggestions.Count > 0)
            {
                var suggestions = string.Join("; ", validation.Suggestions);
                _logger.LogInformation("Network discovery settings suggestions: {Suggestions}", suggestions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save network discovery settings to {Path}", _path);
            throw;
        }
    }
}
