using Microsoft.Extensions.Configuration;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Lightweight provider for system-level settings that must not require database access.
    /// Implementations should read from IConfiguration/IOptions and avoid DbContext usage.
    /// </summary>
    public interface ISystemSettingsProvider
    {
        /// <summary>
        /// Get a settings instance bound from configuration. The provider will attempt to
        /// determine a configuration section name from a public static string property
        /// named "SectionName" on the settings type. If not present, callers can pass
        /// a sectionName into the other overload.
        /// </summary>
        T Get<T>() where T : class, new();

        /// <summary>
        /// Get a settings instance bound from the specified configuration section name.
        /// </summary>
        T Get<T>(string sectionName) where T : class, new();
    }
}
