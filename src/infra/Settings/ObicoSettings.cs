using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

[AppSetting(SectionName)]
[SettingGroup("Monitoring", DisplayName = "Monitoring", Description = "AI-powered monitoring and failure detection", Icon = "pf-icon-monitoring", Order = 5)]
[SettingDisplay(Name = "Obico Failure Detection", Description = "Configure AI-powered print failure detection using Obico ML API.", Icon = "pf-icon-ai", Group = "Monitoring", Order = 1)]
public class ObicoSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "Obico";

    public static string SectionKey => SectionName;

    [SettingDisplay(
        Name = "Enable Obico failure detection",
        Description = "Allow printers with Obico monitoring enabled to be analyzed by the Obico ML API.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [SettingDisplay(
        Name = "Obico ML API URL",
        Description = "Legacy server URL used only when no pooled Obico servers are registered. Register pooled servers to enforce active-print capacity.",
        InputType = SettingInputType.Text)]
    [JsonPropertyName("obicoApiUrl")]
    public string ObicoApiUrl { get; set; } = string.Empty;

    [SettingDisplay(
        Name = "Confidence threshold",
        Description = "Minimum confidence score (0.0-1.0) to trigger failure detection. Higher = fewer false positives.",
        InputType = SettingInputType.Number,
        MinValue = 0.0,
        MaxValue = 1.0)]
    [JsonPropertyName("confidenceThreshold")]
    public decimal ConfidenceThreshold { get; set; } = 0.7m;

    [SettingDisplay(
        Name = "Scan interval", Unit = "seconds",
        Description = "How often to check active print jobs for failures (minimum 10 seconds).",
        InputType = SettingInputType.Number,
        MinValue = 10,
        MaxValue = 300)]
    [JsonPropertyName("scanIntervalSeconds")]
    public int ScanIntervalSeconds { get; set; } = 30;

    [SettingDisplay(
        Name = "Startup delay", Unit = "seconds",
        Description = "How long the monitor waits after application start before beginning scans. Allows the database and printer connections to initialize. Setting this too low causes a burst of errors on every restart.",
        InputType = SettingInputType.Number,
        MinValue = 5,
        MaxValue = 120)]
    [JsonPropertyName("startupDelaySeconds")]
    public int StartupDelaySeconds { get; set; } = 30;

    [SettingDisplay(
        Name = "Print warmup grace period", Unit = "seconds",
        Description = "How long to wait after a print starts before scanning for failures. Skips the nozzle heating, purge line, and first-layer adhesion phases that frequently trigger false positives.",
        InputType = SettingInputType.Number,
        MinValue = 0,
        MaxValue = 600)]
    [JsonPropertyName("warmupGracePeriodSeconds")]
    public int WarmupGracePeriodSeconds { get; set; } = 120;

    [SettingDisplay(
        Name = "Analysis request timeout", Unit = "seconds",
        Description = "Maximum time to wait for the Obico ML API to respond to a single snapshot analysis request. If the ML service is overloaded or unreachable, a long timeout will stall the entire monitoring cycle and delay checks for all other printers.",
        InputType = SettingInputType.Number,
        MinValue = 5,
        MaxValue = 60)]
    [JsonPropertyName("analysisTimeoutSeconds")]
    public int AnalysisTimeoutSeconds { get; set; } = 15;

    public void Validate()
    {
        if (Enabled && !string.IsNullOrWhiteSpace(ObicoApiUrl))
        {
            if (!Uri.TryCreate(ObicoApiUrl, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new System.ComponentModel.DataAnnotations.ValidationException("Obico API URL must be a valid HTTP or HTTPS URL.");
            }
        }

        if (ConfidenceThreshold < 0m || ConfidenceThreshold > 1m)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Confidence threshold must be between 0.0 and 1.0.");
        }

        if (ScanIntervalSeconds < 10 || ScanIntervalSeconds > 300)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Scan interval must be between 10 and 300 seconds.");
        }

        if (StartupDelaySeconds < 5 || StartupDelaySeconds > 120)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Startup delay must be between 5 and 120 seconds.");
        }

        if (WarmupGracePeriodSeconds < 0 || WarmupGracePeriodSeconds > 600)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Warmup grace period must be between 0 and 600 seconds.");
        }

        if (AnalysisTimeoutSeconds < 5 || AnalysisTimeoutSeconds > 60)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Analysis timeout must be between 5 and 60 seconds.");
        }
    }
}
