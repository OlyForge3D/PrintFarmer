using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Initializes IAppSetting instances from environment variables on first run.
    /// Supports the pattern: PFARM__{SettingKey}__{PropertyName}
    /// </summary>
    public class SettingsInitializationService : ISettingsInitializationService
    {
        private readonly IConfiguration _configuration;
        private readonly ISettingsService _settingsService;
        private readonly ILogger<SettingsInitializationService> _logger;

        public SettingsInitializationService(
            IConfiguration configuration,
            ISettingsService settingsService,
            ILogger<SettingsInitializationService> logger)
        {
            _configuration = configuration;
            _settingsService = settingsService;
            _logger = logger;
        }

        /// <summary>
        /// Initializes settings from environment variables if they don't exist in database.
        /// </summary>
        public void InitializeFromEnvironment<T>() where T : class, IAppSetting, new()
        {
            var settingKey = T.SectionKey;

            try
            {
                // Check if settings already exist in database
                var existingSettings = _settingsService.Get<T>();
                if (existingSettings != null && !IsEmpty(existingSettings))
                {
                    _logger.LogDebug("Settings for {SettingKey} already exist in database, skipping environment initialization", settingKey);
                    return;
                }

                // Try to load from environment variables using PFARM__ prefix
                var envPrefix = $"PFARM__{settingKey}";
                var configSection = _configuration.GetSection(envPrefix);

                if (!configSection.Exists())
                {
                    _logger.LogDebug("No environment variables found for {Prefix}, skipping initialization", envPrefix);
                    return;
                }

                // Bind configuration to new settings instance
                var newSettings = new T();
                configSection.Bind(newSettings);

                // Check if we got any non-default values
                if (IsEmpty(newSettings))
                {
                    _logger.LogDebug("Environment variables for {SettingKey} contain only default values, skipping initialization", settingKey);
                    return;
                }

                // Save to database
                _settingsService.Save(newSettings);
                _logger.LogInformation("Initialized {SettingKey} settings from environment variables", settingKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize {SettingKey} from environment variables", settingKey);
            }
        }

        /// <summary>
        /// Checks if a settings object contains only default/empty values.
        /// </summary>
        private static bool IsEmpty<T>(T settings) where T : class
        {
            if (settings == null)
            {
                return true;
            }

            var properties = typeof(T).GetProperties();
            foreach (var prop in properties)
            {
                var value = prop.GetValue(settings);

                // Check for non-empty strings
                if (value is string str && !string.IsNullOrWhiteSpace(str))
                {
                    return false;
                }

                // Check for non-empty collections
                if (value is System.Collections.IEnumerable enumerable && value is not string)
                {
                    var enumerator = enumerable.GetEnumerator();
                    if (enumerator.MoveNext())
                    {
                        return false;
                    }
                }

                // Check for non-zero numbers
                if (value is int intVal && intVal != 0)
                {
                    return false;
                }

                if (value is double doubleVal && Math.Abs(doubleVal) > 0.0001)
                {
                    return false;
                }

                // Check for true booleans (false is default)
                if (value is bool boolVal && boolVal)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
