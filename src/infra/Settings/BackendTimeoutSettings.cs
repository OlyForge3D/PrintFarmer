using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Configurable timeout values for all printer backend operations.
/// All backends (Moonraker, PrusaLink, OctoPrint, SDCP) share these common timeout tiers.
/// </summary>
[AppSetting(SectionName)]
[SettingGroup("Printers", DisplayName = "Printers", Description = "Printer connection and communication settings", Icon = "pf-icon-printer", Order = 1)]
[SettingDisplay(Name = "Backend Timeouts", Description = "Timeout values for printer backend communication.", Icon = "pf-icon-clock", Group = "Printers", Order = 5)]
public class BackendTimeoutSettings : IAppSetting
{
    public const string SectionName = "BackendTimeouts";

    public static string SectionKey => SectionName;

    /// <summary>
    /// Timeout for quick status/health polling operations (seconds).
    /// Used by periodic status checks and connection probes.
    /// </summary>
    [SettingDisplay(Name = "Status Poll Timeout (s)", Description = "Timeout for quick status and health polling operations.", InputType = SettingInputType.Number, MinValue = 1, MaxValue = 60)]
    [JsonPropertyName("statusPollTimeoutSeconds")]
    public int StatusPollTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Timeout for standard commands: metadata, file listing, temperature, movement (seconds).
    /// </summary>
    [SettingDisplay(Name = "Command Timeout (s)", Description = "Timeout for standard printer commands, metadata, and file listing.", InputType = SettingInputType.Number, MinValue = 5, MaxValue = 120)]
    [JsonPropertyName("commandTimeoutSeconds")]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Timeout for print control operations: start, pause, resume, cancel (seconds).
    /// </summary>
    [SettingDisplay(Name = "Print Control Timeout (s)", Description = "Timeout for start, pause, resume, and cancel print operations.", InputType = SettingInputType.Number, MinValue = 10, MaxValue = 300)]
    [JsonPropertyName("printControlTimeoutSeconds")]
    public int PrintControlTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Timeout for file upload operations (seconds).
    /// Large G-code files on slow networks may need generous values.
    /// </summary>
    [SettingDisplay(Name = "File Upload Timeout (s)", Description = "Timeout for uploading G-code files to printers.", InputType = SettingInputType.Number, MinValue = 30, MaxValue = 1800)]
    [JsonPropertyName("fileUploadTimeoutSeconds")]
    public int FileUploadTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Timeout for file download operations (seconds).
    /// Downloading large files from printers over slow connections.
    /// </summary>
    [SettingDisplay(Name = "File Download Timeout (s)", Description = "Timeout for downloading files from printers.", InputType = SettingInputType.Number, MinValue = 60, MaxValue = 3600)]
    [JsonPropertyName("fileDownloadTimeoutSeconds")]
    public int FileDownloadTimeoutSeconds { get; set; } = 900;

    /// <summary>Convenience TimeSpan accessor for status poll timeout.</summary>
    [JsonIgnore]
    public TimeSpan StatusPollTimeout => TimeSpan.FromSeconds(StatusPollTimeoutSeconds);

    [JsonIgnore]
    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(CommandTimeoutSeconds);

    [JsonIgnore]
    public TimeSpan PrintControlTimeout => TimeSpan.FromSeconds(PrintControlTimeoutSeconds);

    [JsonIgnore]
    public TimeSpan FileUploadTimeout => TimeSpan.FromSeconds(FileUploadTimeoutSeconds);

    [JsonIgnore]
    public TimeSpan FileDownloadTimeout => TimeSpan.FromSeconds(FileDownloadTimeoutSeconds);

    /// <summary>
    /// The maximum possible timeout across all categories, plus 30s buffer.
    /// Used as the HttpClient.Timeout ceiling so per-request CTS timeouts control actual cancellation.
    /// </summary>
    [JsonIgnore]
    public TimeSpan HttpClientTimeoutCeiling => TimeSpan.FromSeconds(
        Math.Max(FileUploadTimeoutSeconds, FileDownloadTimeoutSeconds) + 30);
}
