
using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Database configuration settings with validation
/// </summary>
[SystemSetting(SectionName)]
[SettingDisplay(Name = "Database", Description = "Database configuration settings.", Icon = "pf-icon-database", Group = "System", Order = 5)]
public class DatabaseSettings : ISystemSetting, IValidatableSetting
{
    public const string SectionName = "Db";
    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Database Provider", Description = "Type of database provider.", InputType = SettingInputType.Select)]
    [Required]
    [RegularExpression("SqlServer|Postgres|Sqlite")]
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Sqlite";

    [SettingDisplay(Name = "Connection String", Description = "Database connection string.", InputType = SettingInputType.Text)]
    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; set; }

    [SettingDisplay(Name = "Command Timeout (Seconds)", Description = "Timeout for database commands.", InputType = SettingInputType.Number)]
    [Range(1, 300)]
    [JsonPropertyName("commandTimeoutSeconds")]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [SettingDisplay(Name = "Enable Sensitive Data Logging", Description = "Log sensitive data for debugging.", InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enableSensitiveDataLogging")]
    public bool EnableSensitiveDataLogging { get; set; }

    [SettingDisplay(Name = "Initialization Mode", Description = "Database initialization mode.", InputType = SettingInputType.Select)]
    [JsonPropertyName("initMode")]
    public string InitMode { get; set; } = "Migrate";

    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new ValidationException("Provider is required.");
        }
        if (CommandTimeoutSeconds is < 1 or > 300)
        {
            throw new ValidationException("CommandTimeoutSeconds must be between 1 and 300.");
        }
    }
}
