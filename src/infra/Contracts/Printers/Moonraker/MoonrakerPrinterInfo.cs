using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Printer Administration Models
public class MoonrakerPrinterInfo
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("state_message")]
    public string StateMessage { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("software_version")]
    public string SoftwareVersion { get; set; } = string.Empty;

    [JsonPropertyName("cpu_info")]
    public string CpuInfo { get; set; } = string.Empty;

    [JsonPropertyName("klipper_path")]
    public string KlipperPath { get; set; } = string.Empty;

    [JsonPropertyName("python_path")]
    public string PythonPath { get; set; } = string.Empty;

    [JsonPropertyName("log_file")]
    public string LogFile { get; set; } = string.Empty;

    [JsonPropertyName("config_file")]
    public string ConfigFile { get; set; } = string.Empty;
}
