namespace Farm.Web.Shared;

public enum PrinterBackend
{
    Moonraker = 0,
    PrusaLink = 1,
    SDCP = 2
}

public record PrinterDto(
    Guid Id,
    string Name,
    string ServerUrl,
    string? Notes,
    bool IsOnline,
    string? State,
    string? ManufacturerName = null,
    string? ModelName = null,
    double? Progress = null,
    string? JobName = null,
    string? ThumbnailUrl = null,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null,
    PrinterSpoolInfoDto? SpoolInfo = null);

// Basic printer info without live status (for fast loading)
public record PrinterBasicDto(
    Guid Id,
    string Name,
    string ServerUrl,
    string? Notes,
    string? ManufacturerName = null,
    string? ModelName = null,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null);

// Live status info for a specific printer
public record PrinterStatusDto(
    Guid Id,
    bool IsOnline,
    string? State,
    double? Progress = null,
    string? JobName = null,
    string? ThumbnailUrl = null,
    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    double? X = null,
    double? Y = null,
    double? Z = null,
    double? HotendTemp = null,
    double? BedTemp = null,
    double? HotendTarget = null,
    double? BedTarget = null,
    PrinterSpoolInfoDto? SpoolInfo = null);

// Real-time update payload for SignalR
public record PrinterStatusUpdate(
    Guid Id,
    bool IsOnline,
    string? State,
    double? Progress,
    string? JobName,
    string? ThumbnailUrl,
    string? CameraStreamUrl,
    double? X,
    double? Y,
    double? Z,
    double? HotendTemp,
    double? BedTemp,
    double? HotendTarget,
    double? BedTarget,
    PrinterSpoolInfoDto? SpoolInfo);

public class CreatePrinterDto
{
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? OriginalServerUrl { get; set; }
    public string? Notes { get; set; }
    public Guid? ManufacturerId { get; set; }
    public Guid? ModelId { get; set; }
    // Optional: create new manufacturer/model
    public string? NewManufacturerName { get; set; }
    public string? NewModelName { get; set; }
    public DateTime? DateAcquired { get; set; }
    public PrinterBackend Backend { get; set; } = PrinterBackend.Moonraker;
    public string? ApiKey { get; set; }
}

public record UpdatePrinterDto(
    string Name,
    string ServerUrl,
    string? Notes,
    Guid? ManufacturerId,
    Guid? ModelId,
    string? NewManufacturerName,
    string? NewModelName,
    DateTime? DateAcquired,
    PrinterBackend? Backend = null,
    string? ApiKey = null,
    string? OriginalServerUrl = null);

// Local spools removed; Spoolman is the source of truth

public record CommandResult(bool Success, string? Message = null);

public record TempTargets(double? Hotend, double? Bed);
public record MoveRequest(double? X, double? Y, double? Z, double? F);

// Spoolman integration
public record SpoolmanConfigDto(string BaseUrl);
public record SpoolmanSpoolDto(
    int Id,
    string Name,
    string Material,
    double? RemainingWeightG,
    string? ColorHex,
    bool InUse,
    string? FilamentName = null,
    string? Vendor = null,
    DateTime? RegisteredAt = null,
    DateTime? FirstUsedAt = null,
    DateTime? LastUsedAt = null);

// Printer spool information for Moonraker printers
public record PrinterSpoolInfoDto(
    bool HasActiveSpool,
    int? ActiveSpoolId = null,
    string? SpoolName = null,
    string? Material = null,
    string? ColorHex = null,
    string? FilamentName = null,
    string? Vendor = null,
    double? RemainingWeightG = null,
    bool? SpoolInUse = null);

// Catalog (Manufacturers / Models)
public record ManufacturerDto(Guid Id, string Name);
public record ModelDto(Guid Id, string Name, Guid ManufacturerId, double? MaxX = null, double? MaxY = null, double? MaxZ = null, PrinterBackend? DefaultBackend = null);

// Printer details for edit page
public record PrinterDetailsDto(
    Guid Id,
    string Name,
    string ServerUrl,
    string? Notes,
    Guid? ManufacturerId,
    string? ManufacturerName,
    Guid? ModelId,
    string? ModelName,
    double? ModelMaxX,
    double? ModelMaxY,
    double? ModelMaxZ,
    DateTime? DateAcquired,
    PrinterBackend Backend = PrinterBackend.Moonraker,
    string? ApiKey = null,
    string? OriginalServerUrl = null,
    string? IpAddress = null);

// Filament temperature presets (admin-configurable)
public record FilamentPresetsDto(
    TempTargets Abs,
    TempTargets Asa,
    TempTargets Pla,
    TempTargets Pc,
    TempTargets Pctg,
    TempTargets Petg);

// Resolve hostname/IP utility
public record ResolveHostnameRequest(string ServerUrl, PrinterBackend Backend);
public record ResolveHostnameResponse(string NormalizedInputUrl, string? ResolvedIp, string ResolvedBaseUrl);

// Network discovery
public class DiscoveredPrinterDto
{
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
    public PrinterBackend Backend { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? Version { get; set; }
    public bool IsReachable { get; set; }
    public DateTime DiscoveredAt { get; set; }
}

// Network discovery configuration
public record NetworkDiscoverySettingsDto(
    List<string> NetworkRanges,
    int TimeoutMs = 3000,
    int MaxConcurrentScans = 20,
    List<int> Ports = null!)
{
    public NetworkDiscoverySettingsDto() : this(new List<string>(), 3000, 20, new List<int> { 80, 7125 })
    {
    }
}

// History Models (matching Moonraker structure)
public class HistoryListResponse
{
    public int Count { get; set; }
    public HistoryJob[] Jobs { get; set; } = Array.Empty<HistoryJob>();
}

public class HistoryJob
{
    public string JobId { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public double? EndTime { get; set; }
    public double FilamentUsed { get; set; }
    public string Filename { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public double PrintDuration { get; set; }
    public string Status { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double TotalDuration { get; set; }
    public string User { get; set; } = string.Empty;
    public AuxiliaryData[]? AuxiliaryData { get; set; }
    public string? ThumbnailUrl { get; set; }
}

public class AuxiliaryData
{
    public string Provider { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public object Value { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public string? Units { get; set; }
}

public class HistoryTotals
{
    public JobTotals JobTotals { get; set; } = new();
    public AuxiliaryTotals[]? AuxiliaryTotals { get; set; }
}

public class JobTotals
{
    public int TotalJobs { get; set; }
    public double TotalTime { get; set; }
    public double TotalPrintTime { get; set; }
    public double TotalFilamentUsed { get; set; }
    public double LongestJob { get; set; }
    public double LongestPrint { get; set; }
}

public class AuxiliaryTotals
{
    public string Provider { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public double Maximum { get; set; }
    public double Total { get; set; }
}

// G-code Library & Job Queue DTOs
public enum GcodeSourceDto
{
    Upload = 0,
    Harvested = 1,
    Generated = 2
}

public record GcodeFileDto(
    Guid Id,
    string OriginalFileName,
    string DisplayName,
    long FileSizeBytes,
    DateTime UploadedAt,
    GcodeSourceDto Source = GcodeSourceDto.Upload,
    Guid? SourcePrinterId = null,
    string? SourcePrinterName = null,
    string? OriginalPrinterPath = null,
    DateTime? LastSeenOnPrinter = null,
    string? Description = null,
    string[]? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    string[]? CompatibleMaterials = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    double? RequiredBuildVolumeX = null,
    double? RequiredBuildVolumeY = null,
    double? RequiredBuildVolumeZ = null,
    Guid? TargetPrinterId = null,
    string? TargetPrinterName = null,
    Guid? TargetModelId = null,
    string? TargetModelName = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    bool HasThumbnail = false);

public class CreateGcodeFileDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string[]? Tags { get; set; }
    public double? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterial { get; set; }
    public string[]? CompatibleMaterials { get; set; }
    public double? EstimatedPrintTimeMinutes { get; set; }
    public double? EstimatedFilamentLengthMm { get; set; }
    public double? EstimatedFilamentWeightG { get; set; }
    public double? RequiredBuildVolumeX { get; set; }
    public double? RequiredBuildVolumeY { get; set; }
    public double? RequiredBuildVolumeZ { get; set; }
    public Guid? TargetPrinterId { get; set; }
    public Guid? TargetModelId { get; set; }
    public string? SlicerName { get; set; }
    public string? SlicerVersion { get; set; }
    public string? SlicerSettings { get; set; }
}

public record UpdateGcodeFileDto(
    string DisplayName,
    string? Description = null,
    string[]? Tags = null,
    double? RequiredNozzleDiameter = null,
    string? RequiredMaterial = null,
    string[]? CompatibleMaterials = null,
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    double? RequiredBuildVolumeX = null,
    double? RequiredBuildVolumeY = null,
    double? RequiredBuildVolumeZ = null,
    Guid? TargetPrinterId = null,
    Guid? TargetModelId = null,
    string? SlicerName = null,
    string? SlicerVersion = null,
    string? SlicerSettings = null);

// Print Job DTOs
public enum PrintJobStatusDto
{
    Queued = 0,
    Assigned = 1,
    Starting = 2,
    Printing = 3,
    Paused = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7
}

public enum PrintJobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

public record PrintJobDto(
    Guid Id,
    string Name,
    int Priority,
    PrintJobStatusDto Status,
    DateTime QueuedAt,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    string? ErrorMessage = null,
    Guid GcodeFileId = default,
    string GcodeFileName = "",
    Guid? AssignedPrinterId = null,
    string? AssignedPrinterName = null,
    double? HotendTemperature = null,
    double? BedTemperature = null,
    int? SpoolId = null,
    double? ProgressPercentage = null,
    string? CurrentState = null,
    string[]? RequiredCapabilities = null,
    bool AutoAssign = true,
    Guid[]? PreferredPrinterIds = null,
    Guid[]? ExcludedPrinterIds = null);

public class CreatePrintJobDto
{
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; } = 0;
    public Guid GcodeFileId { get; set; }
    public double? HotendTemperature { get; set; }
    public double? BedTemperature { get; set; }
    public int? SpoolId { get; set; }
    public string[]? RequiredCapabilities { get; set; }
    public bool AutoAssign { get; set; } = true;
    public Guid[]? PreferredPrinterIds { get; set; }
    public Guid[]? ExcludedPrinterIds { get; set; }
}

public record UpdatePrintJobDto(
    string Name,
    int Priority,
    double? HotendTemperature = null,
    double? BedTemperature = null,
    int? SpoolId = null,
    string[]? RequiredCapabilities = null,
    bool AutoAssign = true,
    Guid[]? PreferredPrinterIds = null,
    Guid[]? ExcludedPrinterIds = null);

// Printer Capabilities DTOs
public record PrinterCapabilitiesDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
    int? MaxBedTemp = null,
    string? CurrentMaterial = null,
    int? CurrentSpoolId = null,
    bool IsAvailable = true,
    DateTime LastUpdated = default);

public record CreatePrinterCapabilitiesDto(
    Guid PrinterId,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
    int? MaxBedTemp = null);

public record UpdatePrinterCapabilitiesDto(
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int NumberOfExtruders = 1,
    int? MinHotendTemp = null,
    int? MaxHotendTemp = null,
    int? MinBedTemp = null,
    int? MaxBedTemp = null,
    string? CurrentMaterial = null,
    int? CurrentSpoolId = null,
    bool IsAvailable = true);

// Queue Management DTOs
public record QueueStatusDto(
    int TotalJobs,
    int QueuedJobs,
    int ActiveJobs,
    int CompletedJobs,
    int FailedJobs,
    PrintJobDto[] RecentJobs,
    PrinterCapabilitiesDto[] AvailablePrinters);

// G-code Library Search/Filter DTOs
public class GcodeLibrarySearchDto
{
    public string? SearchTerm { get; set; }
    public string[]? Tags { get; set; }
    public string? RequiredMaterial { get; set; }
    public double? NozzleDiameter { get; set; }
    public Guid? TargetPrinterId { get; set; }
    public Guid? TargetModelId { get; set; }
    public DateTime? UploadedAfter { get; set; }
    public DateTime? UploadedBefore { get; set; }
    public int Skip { get; set; } = 0;
    public int Take { get; set; } = 50;
    public string SortBy { get; set; } = "UploadedAt";
    public bool SortDescending { get; set; } = true;
}

public record GcodeLibrarySearchResultDto(
    GcodeFileDto[] Files,
    int TotalCount,
    string[] AvailableTags,
    string[] AvailableMaterials);

// Smart Queue Assignment DTOs
public record QueueAssignmentResultDto(
    bool Success,
    string Message,
    Guid? AssignedPrinterId = null,
    string? AssignedPrinterName = null,
    string[]? MissingCapabilities = null,
    string[]? ConflictingRequirements = null);

// G-code Harvesting DTOs
public enum GcodeHarvestStatusDto
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public record GcodeHarvestOperationDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    DateTime StartedAt,
    DateTime? CompletedAt = null,
    GcodeHarvestStatusDto Status = GcodeHarvestStatusDto.Running,
    string? ErrorMessage = null,
    int FilesFound = 0,
    int FilesAdded = 0,
    int FilesSkipped = 0,
    int FilesErrored = 0,
    long TotalBytesProcessed = 0,
    bool IncludeSubdirectories = true,
    long? MaxFileSizeBytes = null,
    DateTime? ModifiedAfter = null);

public record DiscoveredGcodeFileDto(
    Guid Id,
    Guid HarvestOperationId,
    string PrinterPath,
    string FileName,
    long FileSizeBytes,
    DateTime? ModifiedAt = null,
    string? FileHash = null,
    bool IsSelected = false,
    bool AlreadyInLibrary = false,
    Guid? ExistingLibraryFileId = null,
    bool ProcessingFailed = false,
    string? ErrorMessage = null,
    string? ExtractedSlicerName = null,
    string? ExtractedSlicerVersion = null,
    double? ExtractedPrintTime = null,
    double? ExtractedFilamentLength = null,
    double? ExtractedNozzleDiameter = null,
    string? ExtractedMaterial = null,
    string? ExtractedLayerHeight = null,
    string? ExtractedInfill = null);

public class StartGcodeHarvestDto
{
    public Guid PrinterId { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public DateTime? ModifiedAfter { get; set; }
}

public class ImportSelectedGcodeFilesDto
{
    public Guid HarvestOperationId { get; set; }
    public Guid[] SelectedFileIds { get; set; } = Array.Empty<Guid>();
    public bool AddToLibraryOnly { get; set; } = true; // If false, also create print jobs
    public bool AutoDetectCapabilities { get; set; } = true;
    public string[]? DefaultTags { get; set; }
}

public record GcodeHarvestResultDto(
    Guid OperationId,
    bool Success,
    string Message,
    int DiscoveredFiles = 0,
    int ImportedFiles = 0,
    string[]? Errors = null);

// G-code Metadata Extraction
public record GcodeMetadataDto(
    string? SlicerName = null,
    string? SlicerVersion = null,
    double? PrintTimeMinutes = null,
    double? FilamentLengthMm = null,
    double? FilamentWeightG = null,
    double? NozzleDiameter = null,
    string? Material = null,
    double? LayerHeight = null,
    string? InfillPercentage = null,
    double? PrintSpeed = null,
    double? BedTemperature = null,
    double? HotendTemperature = null,
    double? BuildPlateX = null,
    double? BuildPlateY = null,
    double? BuildPlateZ = null,
    string[]? Objects = null,
    Dictionary<string, object>? AdditionalMetadata = null);

// Job Queue System DTOs
public class JobQueuePrintJobDto
{
    public Guid Id { get; set; }
    public Guid GcodeFileId { get; set; }
    public string GcodeFileName { get; set; } = string.Empty;
    public Guid? AssignedPrinterId { get; set; }
    public string AssignedPrinterName { get; set; } = string.Empty;
    public PrintJobStatusDto Status { get; set; }
    public int Priority { get; set; }
    public int QueuePosition { get; set; }
    public decimal? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterialType { get; set; }
    public TimeSpan? EstimatedPrintTime { get; set; }
    public double? EstimatedFilamentUsage { get; set; }
    public DateTime? ActualStartTime { get; set; }
    public DateTime? ActualEndTime { get; set; }
    public TimeSpan? ActualPrintTime { get; set; }
    public double? ActualFilamentUsage { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Additional Job Queue DTOs
public class QueuePrintJobDto
{
    public Guid GcodeFileId { get; set; }
    public Guid? AssignedPrinterId { get; set; } // If null, auto-assign to best available printer
    public PrintJobPriority Priority { get; set; } = PrintJobPriority.Normal;
    public decimal? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterialType { get; set; }
}

public class UpdatePrintJobStatusDto
{
    public PrintJobStatusDto? Status { get; set; }
    public PrintJobPriority? Priority { get; set; }
    public Guid? AssignedPrinterId { get; set; }
    public double? ActualFilamentUsage { get; set; }
    public string? FailureReason { get; set; }
}

public class ReorderQueueDto
{
    public JobOrderDto[] JobOrder { get; set; } = Array.Empty<JobOrderDto>();
}

public class JobOrderDto
{
    public Guid JobId { get; set; }
    public int Position { get; set; }
}

// Printer Capabilities DTOs
public class CreateOrUpdatePrinterCapabilitiesDto
{
    public decimal[]? NozzleDiameters { get; set; }
    public string[]? SupportedMaterials { get; set; }
    public decimal MaxPrintVolumeX { get; set; }
    public decimal MaxPrintVolumeY { get; set; }
    public decimal MaxPrintVolumeZ { get; set; }
    public int MaxHotendTemperature { get; set; }
    public int MaxBedTemperature { get; set; }
    public bool HasHeatedBed { get; set; }
    public bool HasEnclosure { get; set; }
    public bool SupportsAutoLeveling { get; set; }
    public int MaxPrintSpeed { get; set; }
}

public class PrinterWithCapabilitiesDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public string PrinterModel { get; set; } = string.Empty;
    public PrinterCapabilitiesDto? Capabilities { get; set; }
}

public class CompatiblePrinterDto
{
    public Guid PrinterId { get; set; }
    public string PrinterName { get; set; } = string.Empty;
    public int CompatibilityScore { get; set; } // 0-100
    public string[] CompatibilityReasons { get; set; } = Array.Empty<string>();
    public int CurrentQueueLength { get; set; }
}
