namespace Farm.Web.Api.Domain;

public class Printer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty; // e.g., http://printer:7125 or PrusaLink base URL (IP-resolved)
    public string? OriginalServerUrl { get; set; } // Original URL/host (for re-resolving if IP changes)
    public string? IpAddress { get; set; } // Last resolved IPv4/IPv6 string for convenience
    public string? Notes { get; set; }

    // Backend type (Moonraker or PrusaLink)
    public int Backend { get; set; } = 0; // 0 = Moonraker, 1 = PrusaLink
    public string? ApiKey { get; set; } // For PrusaLink

    // Metadata
    public Guid? ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public Guid? ModelId { get; set; }
    public PrinterModel? Model { get; set; }
    public DateTime? DateAcquired { get; set; }
    
    // Navigation property for capabilities
    public PrinterCapabilities? Capabilities { get; set; }
}

public class Spool
{
    public Guid Id { get; set; }
    public string Material { get; set; } = string.Empty;
    public double WeightGrams { get; set; }
    public string ColorHex { get; set; } = "#000000";
    public bool InUse { get; set; }
    public Guid? AssignedPrinterId { get; set; }
}

public class Manufacturer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<PrinterModel> Models { get; set; } = new List<PrinterModel>();
}

public class PrinterModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public double? MaxX { get; set; }
    public double? MaxY { get; set; }
    public double? MaxZ { get; set; }
    public int? DefaultBackend { get; set; } // Default backend for this model: 0=Moonraker, 1=PrusaLink, 2=SDCP
}

public class SpoolmanConfig
{
    public int Id { get; set; } // Single row table; use Id = 1
    public string BaseUrl { get; set; } = string.Empty;
}

// G-code Library System
public class GcodeFile
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty; // Physical path on disk
    public long FileSizeBytes { get; set; }
    public string FileHash { get; set; } = string.Empty; // SHA256 for deduplication
    public DateTime UploadedAt { get; set; }
    public string? Description { get; set; }
    public string? Tags { get; set; } // JSON array of tags
    
    // Source tracking
    public GcodeSource Source { get; set; } = GcodeSource.Upload;
    public Guid? SourcePrinterId { get; set; } // Printer it was harvested from
    public Printer? SourcePrinter { get; set; }
    public string? OriginalPrinterPath { get; set; } // Original path on the printer
    public DateTime? LastSeenOnPrinter { get; set; } // Last time this file was seen during harvest
    
    // Print Requirements/Capabilities
    public double? RequiredNozzleDiameter { get; set; } // e.g., 0.4mm
    public string? RequiredMaterial { get; set; } // e.g., "PLA", "PETG"
    public string[]? CompatibleMaterials { get; set; } // JSON array of compatible materials
    public double? EstimatedPrintTimeMinutes { get; set; }
    public double? EstimatedFilamentLengthMm { get; set; }
    public double? EstimatedFilamentWeightG { get; set; }
    
    // Build Volume Requirements
    public double? RequiredBuildVolumeX { get; set; }
    public double? RequiredBuildVolumeY { get; set; }
    public double? RequiredBuildVolumeZ { get; set; }
    
    // Printer Compatibility (optional - can target specific printers/models)
    public Guid? TargetPrinterId { get; set; }
    public Printer? TargetPrinter { get; set; }
    public Guid? TargetModelId { get; set; }
    public PrinterModel? TargetModel { get; set; }
    
    // Metadata from slicer
    public string? SlicerName { get; set; } // e.g., "PrusaSlicer", "Cura"
    public string? SlicerVersion { get; set; }
    public string? SlicerSettings { get; set; } // JSON dump of key settings
    
    // Thumbnail
    public string? ThumbnailPath { get; set; } // Path to thumbnail image
    
    // Additional metadata fields
    public double? LayerHeight { get; set; } 
    public double? InfillPercentage { get; set; }
    public double[]? PrintTemperatures { get; set; } // JSON field
    public double? BedTemperature { get; set; }
    public double? PrintSpeed { get; set; }
    public string[]? TargetPrinterModels { get; set; } // JSON field
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum GcodeSource
{
    Upload = 0,      // Manually uploaded by user
    Harvested = 1,   // Harvested from a printer
    Generated = 2    // Generated by the system (future use)
}

// G-code Harvesting System
public class GcodeHarvestOperation
{
    public Guid Id { get; set; }
    public Guid PrinterId { get; set; }
    public Printer Printer { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public GcodeHarvestStatus Status { get; set; } = GcodeHarvestStatus.Running;
    public string? ErrorMessage { get; set; }
    
    // Results
    public int FilesFound { get; set; }
    public int FilesAdded { get; set; }
    public int FilesSkipped { get; set; } // Already in library
    public int FilesErrored { get; set; }
    public long TotalBytesProcessed { get; set; }
    
    // Settings used for this harvest
    public bool IncludeSubdirectories { get; set; } = true;
    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB default
    public DateTime? ModifiedAfter { get; set; } // Only harvest files modified after this date
}

public enum GcodeHarvestStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

// Discovered G-code files during harvest (before adding to library)
public class DiscoveredGcodeFile
{
    public Guid Id { get; set; }
    public Guid HarvestOperationId { get; set; }
    public GcodeHarvestOperation HarvestOperation { get; set; } = null!;
    
    // File info from printer
    public string PrinterPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? FileHash { get; set; } // Calculated if downloaded
    
    // Processing status
    public bool IsSelected { get; set; } = false; // User selection for import
    public bool AlreadyInLibrary { get; set; } = false;
    public Guid? ExistingLibraryFileId { get; set; }
    public bool ProcessingFailed { get; set; } = false;
    public string? ErrorMessage { get; set; }
    
    // Extracted metadata (from G-code header analysis)
    public string? ExtractedSlicerName { get; set; }
    public string? ExtractedSlicerVersion { get; set; }
    public double? ExtractedPrintTime { get; set; }
    public double? ExtractedFilamentLength { get; set; }
    public double? ExtractedNozzleDiameter { get; set; }
    public string? ExtractedMaterial { get; set; }
    public string? ExtractedLayerHeight { get; set; }
    public string? ExtractedInfill { get; set; }
}

// Job Queue System  
public class PrintJob
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty; // Display name for the job
    public Guid GcodeFileId { get; set; }
    public GcodeFile GcodeFile { get; set; } = null!;
    
    // Printer Assignment
    public Guid? AssignedPrinterId { get; set; }
    public Printer? AssignedPrinter { get; set; }
    
    // Queue Management
    public PrintJobStatus Status { get; set; } = PrintJobStatus.Queued;
    public int Priority { get; set; } = 0; // Higher = more important
    public int QueuePosition { get; set; } = 0;
    
    // Requirements
    public decimal? RequiredNozzleDiameter { get; set; }
    public string? RequiredMaterialType { get; set; }
    public string[]? RequiredCapabilities { get; set; } // JSON array of required capabilities
    
    // Estimates (from G-code file)
    public TimeSpan? EstimatedPrintTime { get; set; }
    public double? EstimatedFilamentUsage { get; set; }
    
    // Actual values (reported during/after print)
    public DateTime? ActualStartTime { get; set; }
    public DateTime? ActualEndTime { get; set; }
    public TimeSpan? ActualPrintTime { get; set; }
    public double? ActualFilamentUsage { get; set; }
    public string? FailureReason { get; set; }
    
    // Preferences for printer selection
    public Guid[]? PreferredPrinterIds { get; set; } // JSON array of preferred printer IDs
    public Guid[]? ExcludedPrinterIds { get; set; } // JSON array of excluded printer IDs
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime QueuedAt { get; set; }
}

public enum PrintJobStatus
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

// Printer Capabilities (extends Printer entity conceptually)
public class PrinterCapabilities
{
    public Guid Id { get; set; }
    public Guid PrinterId { get; set; }
    public Printer Printer { get; set; } = null!;
    
    // Physical capabilities
    public double? NozzleDiameter { get; set; }
    public string[]? SupportedMaterials { get; set; } // JSON array: ["PLA", "PETG", "ABS"]
    public double? MaxBuildVolumeX { get; set; }
    public double? MaxBuildVolumeY { get; set; }
    public double? MaxBuildVolumeZ { get; set; }
    
    // Advanced capabilities
    public bool HasHeatedBed { get; set; } = true;
    public bool HasEnclosure { get; set; } = false;
    public bool MultiMaterial { get; set; } = false;
    public int NumberOfExtruders { get; set; } = 1;
    
    // Temperature ranges
    public int? MinHotendTemp { get; set; }
    public int? MaxHotendTemp { get; set; }
    public int? MinBedTemp { get; set; }
    public int? MaxBedTemp { get; set; }
    
    // Current state for queue matching
    public string? CurrentMaterial { get; set; } // From Spoolman integration
    public int? CurrentSpoolId { get; set; } // Spoolman spool ID
    public bool IsAvailable { get; set; } = true; // Can accept new jobs
    public DateTime LastUpdated { get; set; }
    
    // Additional capability fields
    public bool SupportsAutoLeveling { get; set; } = false;
    public int? MaxPrintSpeed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
