using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace Farm.Web.Api.Domain;

public class Printer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use ServerUri for typed access")]
    public string ServerUrl { get; set; } = string.Empty; // e.g., http://printer:7125 or PrusaLink base URL (IP-resolved)
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use OriginalServerUri for typed access")]
    public string? OriginalServerUrl { get; set; } // Original URL/host (for re-resolving if IP changes)

    [NotMapped]
    public Uri? ServerUri
    {
        get => Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri? u) ? u : null;
        set => ServerUrl = value?.ToString() ?? string.Empty;
    }

    [NotMapped]
    public Uri? OriginalServerUri
    {
        get => string.IsNullOrWhiteSpace(OriginalServerUrl) ? null : (Uri.TryCreate(OriginalServerUrl, UriKind.Absolute, out Uri? u) ? u : null);
        set => OriginalServerUrl = value?.ToString();
    }
    public string? IpAddress { get; set; } // Last resolved IPv4/IPv6 string for convenience
    public string? Notes { get; set; }

    // Backend type (Moonraker or PrusaLink)
    public int Backend { get; set; } // 0 = Moonraker, 1 = PrusaLink
    public string? ApiKey { get; set; } // For PrusaLink

    // Metadata
    public Guid ManufacturerId { get; set; } // No longer nullable - uses default "Unknown" manufacturer
    public Manufacturer? Manufacturer { get; set; }
    public Guid ModelId { get; set; } // No longer nullable - uses default "Unknown Model"
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
    public ICollection<PrinterModel> Models { get; } = new List<PrinterModel>();
}

public class PrinterModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public int? Type { get; set; } // PrinterType enum: 0=Cartesian, 1=CoreXY, 2=Delta, 3=Polar, 4=SCARA, 99=Unknown
    public double? MaxX { get; set; }
    public double? MaxY { get; set; }
    public double? MaxZ { get; set; }
    public int? DefaultBackend { get; set; } // Default backend for this model: 0=Moonraker, 1=PrusaLink, 2=SDCP
    public ICollection<PrinterModelFilamentType> SupportedFilamentTypes { get; } = new List<PrinterModelFilamentType>();
}

public class FilamentType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double? DefaultHotendTemp { get; set; }
    public double? DefaultBedTemp { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PrinterModelFilamentType> PrinterModels { get; } = new List<PrinterModelFilamentType>();
}

public class PrinterModelFilamentType
{
    public Guid PrinterModelId { get; set; }
    public PrinterModel? PrinterModel { get; set; }
    public Guid FilamentTypeId { get; set; }
    public FilamentType? FilamentType { get; set; }
}

public class SpoolmanConfig
{
    public int Id { get; set; } // Single row table; use Id = 1
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use BaseUri for typed access")]
    public string BaseUrl { get; set; } = string.Empty;

    [NotMapped]
    public Uri? BaseUri
    {
        get => Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? u) ? u : null;
        set => BaseUrl = value?.ToString() ?? string.Empty;
    }
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
    public GcodeSource Source { get; set; }
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
    public GcodeHarvestStatus Status { get; set; }
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
    public string[]? FileExtensions { get; set; } // JSON stored list of allowed extensions (without dot)
    public long? MinFileSizeBytes { get; set; }
    public string? DuplicateHandling { get; set; }
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
    public bool IsSelected { get; set; } // User selection for import
    public bool AlreadyInLibrary { get; set; }
    public Guid? ExistingLibraryFileId { get; set; }
    public bool ProcessingFailed { get; set; }
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

// 3D Model Management System
public class Model3D
{
    public Guid Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty; // Physical path on disk
    public long FileSizeBytes { get; set; }
    public string FileHash { get; set; } = string.Empty; // SHA256 for deduplication
    public ModelFileFormat FileFormat { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? Description { get; set; }
    public string? Tags { get; set; } // JSON array of tags

    // Model Properties
    public double? DimensionX { get; set; } // in mm
    public double? DimensionY { get; set; } // in mm  
    public double? DimensionZ { get; set; } // in mm
    public double? VolumeM3 { get; set; } // in cubic mm
    public int? TriangleCount { get; set; }
    public bool IsValid { get; set; } = true;
    public string? ValidationErrors { get; set; } // JSON array of validation issues

    // Thumbnail
    public string? ThumbnailPath { get; set; } // Path to thumbnail image

    // User/Owner tracking
    public Guid? UploadedByUserId { get; set; }
    public User? UploadedByUser { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum ModelFileFormat
{
    STL = 0,
    TMF = 1,  // 3MF
    OBJ = 2,
    PLY = 3,
    STEP = 4
}

// Slicer Profile Management System
public class SlicerProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SlicerType SlicerType { get; set; }

    // Printer Compatibility
    public Guid? PrinterModelId { get; set; }
    public PrinterModel? PrinterModel { get; set; }
    public Guid? SpecificPrinterId { get; set; } // Optional: specific printer instance
    public Printer? SpecificPrinter { get; set; }

    // Basic Settings
    public double LayerHeight { get; set; } = 0.2; // in mm
    public int InfillPercentage { get; set; } = 20; // 0-100%
    public double PrintSpeed { get; set; } = 50; // mm/s
    public int NozzleTemperature { get; set; } = 210; // °C
    public int BedTemperature { get; set; } = 60; // °C
    public bool EnableSupports { get; set; }
    public string Material { get; set; } = "PLA";
    public ProfileQuality Quality { get; set; } = ProfileQuality.Standard;

    // Advanced Settings (JSON storage for extensibility)
    public string? AdvancedSettings { get; set; } // JSON object with additional slicer-specific settings

    // Profile Management
    public bool IsDefault { get; set; }
    public bool IsPublic { get; set; } = true; // Can be used by other users
    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum SlicerType
{
    PrusaSlicer = 0,
    OrcaSlicer = 1,
    Cura = 2,
    SuperSlicer = 3
}

public enum ProfileQuality
{
    Draft = 0,
    Standard = 1,
    Fine = 2
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
    public PrintJobStatus Status { get; set; }
    public int Priority { get; set; } // Higher = more important
    public int QueuePosition { get; set; }

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
    public bool HasEnclosure { get; set; }
    public bool MultiMaterial { get; set; }
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
    public bool SupportsAutoLeveling { get; set; }
    public int? MaxPrintSpeed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// User Management and Authentication System
public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool IsActive { get; set; } = true;
    public bool EmailConfirmed { get; set; }
    public string? EmailConfirmationToken { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetExpires { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
}

public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystemRole { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<UserRole> UserRoles { get; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public class Resource
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ResourceType { get; set; } = string.Empty; // 'printer', 'harvest', 'slicer', 'system'
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public class Action
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<RolePermission> RolePermissions { get; } = new List<RolePermission>();
}

public class RolePermission
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public Guid ResourceId { get; set; }
    public Resource Resource { get; set; } = null!;
    public Guid ActionId { get; set; }
    public Action Action { get; set; } = null!;
    public bool Granted { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public class UserRole
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public DateTime AssignedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
}
