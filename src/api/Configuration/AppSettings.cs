using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Configuration;

/// <summary>
/// Application configuration settings with validation
/// </summary>
public class AppSettings
{
    public const string SectionName = "App";

    [Required]
    [Range(1, 65535)]
    public int Port { get; set; } = 5088;

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "http://localhost:5088";

    [Required]
    [MinLength(1)]
    public string AllowedOrigins { get; set; } = "*";

    [Range(1, 3600)]
    public int HttpTimeoutSeconds { get; set; } = 30;

    [Range(1, 100)]
    public int MaxConcurrentConnections { get; set; } = 50;

    [Range(10, 86400)]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public bool EnableDetailedErrors { get; set; } = false;
    
    [Range(1, 10000)]
    public int MaxRetryAttempts { get; set; } = 10;
}

/// <summary>
/// Database configuration settings with validation
/// </summary>
public class DatabaseSettings
{
    public const string SectionName = "Db";

    [Required]
    [AllowedValues("SqlServer", "Postgres", "MySql", "Sqlite")]
    public string Provider { get; set; } = "Sqlite";

    public string? ConnectionString { get; set; }

    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    public bool EnableSensitiveDataLogging { get; set; } = false;
    
    public string InitMode { get; set; } = "Migrate";
}

/// <summary>
/// Configuration validation service that runs at startup
/// </summary>
public class ConfigurationValidator(IOptions<AppSettings> appSettings, IOptions<DatabaseSettings> dbSettings, ILogger<ConfigurationValidator> logger)
{
    public void ValidateConfiguration()
    {
        logger.LogInformation("Validating application configuration...");

        var validationErrors = new List<string>();

        // Validate app settings
        var appConfig = appSettings.Value;
        if (appConfig.Port < 1 || appConfig.Port > 65535)
            validationErrors.Add($"Invalid port: {appConfig.Port}. Must be between 1-65535");

        if (!Uri.TryCreate(appConfig.BaseUrl, UriKind.Absolute, out var baseUri))
            validationErrors.Add($"Invalid BaseUrl: {appConfig.BaseUrl}");

        // Validate database settings
        var dbConfig = dbSettings.Value;
        var validProviders = new[] { "SqlServer", "Postgres", "MySql", "Sqlite" };
        if (!validProviders.Contains(dbConfig.Provider))
            validationErrors.Add($"Invalid database provider: {dbConfig.Provider}. Must be one of: {string.Join(", ", validProviders)}");

        if (dbConfig.Provider != "Sqlite" && string.IsNullOrWhiteSpace(dbConfig.ConnectionString))
            validationErrors.Add($"Connection string required for provider: {dbConfig.Provider}");

        // Log warnings for development settings in production
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production")
        {
            if (appConfig.EnableDetailedErrors)
                logger.LogWarning("Detailed errors are enabled in production environment");

            if (dbConfig.EnableSensitiveDataLogging)
                logger.LogWarning("Sensitive data logging is enabled in production environment");

            if (appConfig.AllowedOrigins == "*")
                logger.LogWarning("CORS is configured to allow all origins in production environment");
        }

        if (validationErrors.Any())
        {
            var errorMessage = $"Configuration validation failed:\n{string.Join("\n", validationErrors)}";
            logger.LogCritical(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        logger.LogInformation("Configuration validation completed successfully");
        logger.LogInformation("App Settings: Port={Port}, BaseUrl={BaseUrl}, HttpTimeout={Timeout}s", 
            appConfig.Port, appConfig.BaseUrl, appConfig.HttpTimeoutSeconds);
        logger.LogInformation("Database Settings: Provider={Provider}, InitMode={InitMode}", 
            dbConfig.Provider, dbConfig.InitMode);
    }
}
