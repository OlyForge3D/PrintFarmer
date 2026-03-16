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
        Name = "Enable Failure Detection",
        Description = "Enable automatic AI-powered failure detection for active print jobs.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [SettingDisplay(
        Name = "Obico ML API URL",
        Description = "URL of the Obico ML API server (default: http://obico-ml-api:3333).",
        InputType = SettingInputType.Text)]
    [JsonPropertyName("obicoApiUrl")]
    public string ObicoApiUrl { get; set; } = "http://obico-ml-api:3333";

    [SettingDisplay(
        Name = "Confidence Threshold",
        Description = "Minimum confidence score (0.0-1.0) to trigger failure detection. Higher = fewer false positives.",
        InputType = SettingInputType.Number,
        MinValue = 0.0,
        MaxValue = 1.0)]
    [JsonPropertyName("confidenceThreshold")]
    public decimal ConfidenceThreshold { get; set; } = 0.7m;

    [SettingDisplay(
        Name = "Scan Interval (seconds)",
        Description = "How often to check active print jobs for failures (minimum 10 seconds).",
        InputType = SettingInputType.Number,
        MinValue = 10,
        MaxValue = 300)]
    [JsonPropertyName("scanIntervalSeconds")]
    public int ScanIntervalSeconds { get; set; } = 30;

    [SettingDisplay(
        Name = "Auto-Pause on Failure",
        Description = "Automatically pause the print job when a failure is detected.",
        InputType = SettingInputType.Boolean)]
    [JsonPropertyName("autoPauseOnFailure")]
    public bool AutoPauseOnFailure { get; set; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ObicoApiUrl))
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Obico API URL is required.");
        }

        if (!Uri.TryCreate(ObicoApiUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Obico API URL must be a valid HTTP or HTTPS URL.");
        }

        if (ConfidenceThreshold < 0m || ConfidenceThreshold > 1m)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Confidence threshold must be between 0.0 and 1.0.");
        }

        if (ScanIntervalSeconds < 10 || ScanIntervalSeconds > 300)
        {
            throw new System.ComponentModel.DataAnnotations.ValidationException("Scan interval must be between 10 and 300 seconds.");
        }
    }
}
