using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Farm.Web.Api.Services;

// Version Information
public class VersionInfo
{
    public string Api { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Printer { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    public string? Sdk { get; set; }
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "DTO for JSON transport; setter needed for deserialization")]
    public Dictionary<string, object> Capabilities { get; set; } = new Dictionary<string, object>();
}

// Printer Information
public class PrinterInfo
{
    public bool Mmu { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public bool FarmMode { get; set; }
    public double NozzleDiameter { get; set; }
    public int MinExtrusionTemp { get; set; }
    public string Serial { get; set; } = string.Empty;
    public bool SdReady { get; set; }
    public bool ActiveCamera { get; set; }
    public string? Hostname { get; set; }
    public string? Port { get; set; }
    public bool NetworkErrorChime { get; set; }
}

// Status Information
public class StatusInfo
{
    public StatusJob? Job { get; set; }
    public StatusPrinterInfo Printer { get; set; } = new();
    public StatusTransfer? Transfer { get; set; }
    public StatusStorage? Storage { get; set; }
    public StatusCamera? Camera { get; set; }
}

public class StatusJob
{
    public int? Id { get; set; }
    public double? Progress { get; set; }
    public int? TimeRemaining { get; set; }
    public int? TimePrinting { get; set; }
}

public class StatusPrinterInfo
{
    public string State { get; set; } = string.Empty;
    public double? TempNozzle { get; set; }
    public double? TargetNozzle { get; set; }
    public double? TempBed { get; set; }
    public double? TargetBed { get; set; }
    public double? AxisX { get; set; }
    public double? AxisY { get; set; }
    public double? AxisZ { get; set; }
    public int? Flow { get; set; }
    public int? Speed { get; set; }
    public int? FanHotend { get; set; }
    public int? FanPrint { get; set; }
    public PrinterStatusInfo? StatusPrinter { get; set; }
    public PrinterStatusInfo? StatusConnect { get; set; }
}

public class PrinterStatusInfo
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class StatusTransfer
{
    public int Id { get; set; }
    public int TimeTransferring { get; set; }
    public double? Progress { get; set; }
    public long? DataTransferred { get; set; }
}

public class StatusStorage
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public long? FreeSpace { get; set; }
}

public class StatusCamera
{
    public string Id { get; set; } = string.Empty;
}

// Job Information
public abstract class JobBase
{
    public int Id { get; set; }
    public string State { get; set; } = string.Empty;
    public double Progress { get; set; }
    public int TimePrinting { get; set; }
    public int? TimeRemaining { get; set; }
    public bool InaccurateEstimates { get; set; }
}

public class JobSerialPrint : JobBase
{
    public bool SerialPrint { get; set; }
}

public class JobFilePrint : JobBase
{
    public JobFile File { get; set; } = new();
}

public class JobFile
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? DisplayPath { get; set; }
    public long Size { get; set; }
    public long MTimestamp { get; set; }
    public PrintFileMetadata? Meta { get; set; }
    public PrintFileRefs? Refs { get; set; }
}

// Use JobFilePrint as the default Job type since it's more common

[SuppressMessage("Major Code Smell", "S2094:Classes should not be empty", Justification = "Convenience wrapper to keep API shape consistent")]
public class Job : JobFilePrint { }

// Storage
public class StorageListResponse
{
    public Storage[] StorageList { get; set; } = Array.Empty<Storage>();
}

[SuppressMessage("Naming", "CA1724:Type names should not conflict with namespaces", Justification = "Matches upstream API schema; renaming would be a breaking change.")]
public class Storage
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long? PrintFiles { get; set; }
    public long? SystemFiles { get; set; }
    public long? FreeSpace { get; set; }
    public long? TotalSpace { get; set; }
    public bool Available { get; set; }
    public bool ReadOnly { get; set; }
}

// Transfer
public class Transfer
{
    public string Type { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Source API shape; keep string for JSON")] public string? Url { get; set; }
    public long? Size { get; set; }
    public double Progress { get; set; }
    public long Transferred { get; set; }
    public int? TimeRemaining { get; set; }
    public int TimeTransferring { get; set; }
    public bool ToPrint { get; set; }
}

// File Information Base Classes
public abstract class FileInfoBase
{
    public string Name { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public long? Size { get; set; }
    public string Type { get; set; } = string.Empty;
    public long MTimestamp { get; set; }
    public string? DisplayName { get; set; }
}

public class FileInfo : FileInfoBase
{
    public FileRefs? Refs { get; set; }
}

public class FileRefs
{
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Source API shape; keep string for JSON")] public string? Download { get; set; }
}

public class PrintFileInfo : FileInfoBase
{
    public PrintFileRefs? Refs { get; set; }
    public PrintFileMetadata? Meta { get; set; }
}

public class PrintFileRefs
{
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Source API shape; keep string for JSON")] public string? Download { get; set; }
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Source API shape; keep string for JSON")] public string? Icon { get; set; }
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Source API shape; keep string for JSON")] public string? Thumbnail { get; set; }
}

public class PrintFileMetadata
{
    public int? BedTemperature { get; set; }
    public int[]? BedTemperaturePerTool { get; set; }
    public int? Temperature { get; set; }
    public int[]? TemperaturePerTool { get; set; }
    public int? BrimWidth { get; set; }

    [JsonPropertyName("estimated printing time (normal mode)")]
    public string? EstimatedPrintingTimeNormal { get; set; }

    public int? EstimatedPrintTime { get; set; }
    public int? FadedLayers { get; set; }

    [JsonPropertyName("filament cost")]
    public double? FilamentCost { get; set; }

    [JsonPropertyName("filament cost per tool")]
    public double[]? FilamentCostPerTool { get; set; }

    [JsonPropertyName("filament used [cm3]")]
    public double? FilamentUsedCm3 { get; set; }

    [JsonPropertyName("filament used [cm3] per tool")]
    public double[]? FilamentUsedCm3PerTool { get; set; }

    [JsonPropertyName("filament used [g]")]
    public double? FilamentUsedG { get; set; }

    [JsonPropertyName("filament used [g] per tool")]
    public double[]? FilamentUsedGPerTool { get; set; }

    [JsonPropertyName("filament used [mm]")]
    public double? FilamentUsedMm { get; set; }

    [JsonPropertyName("filament used [mm] per tool")]
    public double[]? FilamentUsedMmPerTool { get; set; }

    public string? FilamentType { get; set; }
    public string[]? FilamentTypePerTool { get; set; }
    public string? FillDensity { get; set; }
    public int? InitialExposureTime { get; set; }
    public double? LayerHeight { get; set; }
    public string? MaterialName { get; set; }
    public int? ExposureTime { get; set; }
    public int? MaxExposureTime { get; set; }
    public int? MaxInitialExposureTime { get; set; }
    public int? MinExposureTime { get; set; }
    public int? MinInitialExposureTime { get; set; }
    public double? NozzleDiameter { get; set; }
    public double[]? NozzleDiameterPerTool { get; set; }
    public bool? NormalPercentPresent { get; set; }
    public bool? NormalLeftPresent { get; set; }
    public bool? QuietPercentPresent { get; set; }
    public bool? QuietLeftPresent { get; set; }
    public bool? LayerInfoPresent { get; set; }
    public double? MaxLayerZ { get; set; }
    public int? PrintTime { get; set; }
    public string? PrinterModel { get; set; }
    public string? SupportMaterial { get; set; }
    public int? Ironing { get; set; }
    public double? RequiredResinMl { get; set; }
    public string? Profile { get; set; }
}

public class FirmwareFileInfo : FileInfoBase
{
    public FirmwareFileRefs? Refs { get; set; }
    public FirmwareMetadata? Meta { get; set; }
}

public class FirmwareFileRefs
{
    public string? Download { get; set; }
}

public class FirmwareMetadata
{
    public string? Version { get; set; }
    public int? PrinterType { get; set; }
    public int? PrinterVersion { get; set; }
}

public class FolderInfo : FileInfoBase
{
    public FileInfoBase[] Children { get; set; } = Array.Empty<FileInfoBase>();
}

public record FileStatus(bool Exists, bool ReadOnly, bool CurrentlyPrinted);

// Camera Management
public class Camera
{
    public string CameraId { get; set; } = string.Empty;
    public CameraConfigInfo? Config { get; set; }
    public bool Connected { get; set; }
    public bool Detected { get; set; }
    public bool Stored { get; set; }
    public bool Linked { get; set; }
}

public class CameraConfigInfo
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
}

public class CameraConfig
{
    public string Name { get; set; } = string.Empty;
    public string TriggerScheme { get; set; } = string.Empty;
    public CameraResolution[] AvailableResolutions { get; set; } = Array.Empty<CameraResolution>();
    public CameraResolution Resolution { get; set; } = new();
    public double Focus { get; set; }
    public string[] Capabilities { get; set; } = Array.Empty<string>();
}

public class CameraConfigSet
{
    public string? Name { get; set; }
    public string? TriggerScheme { get; set; }
    public CameraResolution? Resolution { get; set; }
    public int? Rotation { get; set; }
    public double? Focus { get; set; }
    public double? Exposure { get; set; }
    public bool? SendToConnect { get; set; }
}

public class CameraResolution
{
    public int Width { get; set; }
    public int Height { get; set; }
}

// Update Management
public class UpdateInfo
{
    public string? NewVersion { get; set; }
    public bool UpdateAvailable { get; set; }
}

// Error Handling
public class PrusaLinkError
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Source API shape; keep string for JSON")] public string? Url { get; set; }
}

// Enums based on OpenAPI spec
public static class PrinterStates
{
    public const string Idle = "IDLE";
    public const string Busy = "BUSY";
    public const string Printing = "PRINTING";
    public const string Paused = "PAUSED";
    public const string Finished = "FINISHED";
    public const string Stopped = "STOPPED";
    public const string Error = "ERROR";
    public const string Attention = "ATTENTION";
    public const string Ready = "READY";
}

public static class JobStates
{
    public const string Printing = "PRINTING";
    public const string Paused = "PAUSED";
    public const string Finished = "FINISHED";
    public const string Stopped = "STOPPED";
    public const string Error = "ERROR";
}

public static class FileTypes
{
    public const string PrintFile = "PRINT_FILE";
    public const string Firmware = "FIRMWARE";
    public const string File = "FILE";
    public const string Folder = "FOLDER";
}

public static class StorageTypes
{
    public const string Local = "LOCAL";
    public const string SdCard = "SDCARD";
    public const string Usb = "USB";
}

public static class TransferTypes
{
    public const string NoTransfer = "NO_TRANSFER";
    public const string FromWeb = "FROM_WEB";
    public const string FromConnect = "FROM_CONNECT";
    public const string FromPrinter = "FROM_PRINTER";
    public const string FromSlicer = "FROM_SLICER";
    public const string FromClient = "FROM_CLIENT";
    public const string ToConnect = "TO_CONNECT";
    public const string ToClient = "TO_CLIENT";
}

public static class TriggerSchemes
{
    public const string TenSec = "TEN_SEC";
    public const string ThirtySec = "THIRTY_SEC";
    public const string SixtySec = "SIXTY_SEC";
    public const string EachLayer = "EACH_LAYER";
    public const string FifthLayer = "FIFTH_LAYER";
    public const string Manual = "MANUAL";
}

public static class CameraCapabilities
{
    public const string TriggerScheme = "TRIGGER_SCHEME";
    public const string Imaging = "IMAGING";
    public const string Resolution = "RESOLUTION";
    public const string Rotation = "ROTATION";
    public const string Exposure = "EXPOSURE";
    public const string Focus = "FOCUS";
}
