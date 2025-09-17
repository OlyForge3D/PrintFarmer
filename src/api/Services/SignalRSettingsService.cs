using System.Text.Json;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

public class SignalRSettingsService : ISignalRSettingsService
{
    private readonly ILogger<SignalRSettingsService> _logger;
    private SignalRSettingsDto? _settings;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "signalr.settings.json");
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    public SignalRSettingsService(ILogger<SignalRSettingsService> logger)
    {
        _logger = logger;
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                SignalRSettingsDto? cfg = JsonSerializer.Deserialize<SignalRSettingsDto>(json);
                if (cfg is not null)
                {
                    _settings = cfg;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load SignalR settings from {Path}", _path);
        }
    }

    public SignalRSettingsDto GetSettings()
    {
        if (_settings != null)
        {
            return _settings;
        }

        // Default SignalR settings
        // Allow override via environment variables
        string? envLogLevel = Environment.GetEnvironmentVariable("SIGNALR_LOG_LEVEL");
        string? envConsoleLogging = Environment.GetEnvironmentVariable("SIGNALR_CONSOLE_LOGGING");

        string logLevel = "Information";
        if (!string.IsNullOrWhiteSpace(envLogLevel))
        {
            string[] validLevels = new[] { "Debug", "Information", "Warning", "Error", "None" };
            if (validLevels.Contains(envLogLevel, StringComparer.OrdinalIgnoreCase))
            {
                logLevel = envLogLevel;
            }
        }

        bool consoleLogging = true;
        if (!string.IsNullOrWhiteSpace(envConsoleLogging))
        {
            consoleLogging = string.Equals(envConsoleLogging, "true", StringComparison.OrdinalIgnoreCase);
        }

        return new SignalRSettingsDto(logLevel, consoleLogging);
    }

    public void SaveSettings(SignalRSettingsDto settings)
    {
        _settings = settings;
        try
        {
            string json = JsonSerializer.Serialize(_settings, s_writeOptions);
            File.WriteAllText(_path, json);
            _logger.LogInformation("Saved SignalR settings to {Path}", _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save SignalR settings to {Path}", _path);
        }
    }
}
