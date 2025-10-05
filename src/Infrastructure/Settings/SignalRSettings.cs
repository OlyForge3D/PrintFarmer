using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings
{
    [AppSetting(SectionName)]
    [SettingDisplay(Name = "SignalR", Description = "Settings for SignalR real-time communication.", Icon = "pf-icon-signalr", Group = "Networking", Order = 2)]
    public class SignalRSettings : IAppSetting
    {
        public const string SectionName = "SignalR";
        public static string SectionKey => SectionName;
        [SettingDisplay(
            Name = "Log Level",
            Description = "Minimum log level for SignalR events.",
            AllowedValues = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" },
            InputType = SettingInputType.Select)]
        [JsonPropertyName("logLevel")]
        public string LogLevel { get; set; } = "Information";

        [SettingDisplay(
            Name = "Console Logging Enabled",
            Description = "Enable logging to console for SignalR.",
            InputType = SettingInputType.Boolean)]
        [JsonPropertyName("consoleLoggingEnabled")]
        public bool ConsoleLoggingEnabled { get; set; } = true;
    }
}
