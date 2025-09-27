
using System;
using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Database configuration settings with validation
    /// </summary>
    [AppSetting(SectionName)]
    public class DatabaseSettings : IAppSetting, IValidatableSetting
    {
        public const string SectionName = "Db";
        public static string SectionKey => SectionName;

        [Required]
        [RegularExpression("SqlServer|Postgres|MySql|Sqlite")]
        public string Provider { get; set; } = "Sqlite";

        public string? ConnectionString { get; set; }

        [Range(1, 300)]
        public int CommandTimeoutSeconds { get; set; } = 30;

        public bool EnableSensitiveDataLogging { get; set; }

        public string InitMode { get; set; } = "Migrate";

        public void Validate()
        {
            Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
            if (string.IsNullOrWhiteSpace(Provider))
            {
                throw new ValidationException("Provider is required.");
            }
            if (CommandTimeoutSeconds < 1 || CommandTimeoutSeconds > 300)
            {
                throw new ValidationException("CommandTimeoutSeconds must be between 1 and 300.");
            }
        }
    }
}
