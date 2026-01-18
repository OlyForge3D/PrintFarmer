using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class Printer
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use ServerUri for typed access")]
    public string ServerUrl { get; set; } = string.Empty; // e.g., http://printer:7125 or PrusaLink base URL (IP-resolved)

    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use OriginalServerUri for typed access")]
    public string? OriginalServerUrl { get; set; } // Original URL/host (for re-resolving if IP changes)

    public int BackendPort { get; set; } // Port for backend connection: 7125 (Moonraker), 80 (PrusaLink/OctoPrint), 8080 (SDCP). ALWAYS SET BY DISCOVERY PROBES - NEVER DEFAULT!

    public int? FrontendPort { get; set; } // null for non-Moonraker, 80 for Moonraker by default

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

    /// <summary>
    /// Constructs the backend URL by combining ServerUrl with BackendPort.
    /// Omits port if it's a default port (80 for HTTP, 443 for HTTPS).
    /// </summary>
    [NotMapped]
    public string BackendUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ServerUrl))
            {
                return ServerUrl;
            }

            try
            {
                Uri baseUri = new(ServerUrl);
                int defaultPort = baseUri.Scheme == "https" ? 443 : 80;

                // Only include port in URL if it's non-standard
                if (BackendPort == defaultPort)
                {
                    return baseUri.ToString().TrimEnd('/');
                }

                UriBuilder ub = new(baseUri) { Port = BackendPort };
                return ub.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return ServerUrl;
            }
        }
    }

    /// <summary>
    /// Constructs the frontend URL by combining ServerUrl with FrontendPort.
    /// For Moonraker, this typically points to the web UI on port 80.
    /// Returns BackendUrl if FrontendPort is not set.
    /// </summary>
    [NotMapped]
    public string FrontendUrl
    {
        get
        {
            if (!FrontendPort.HasValue || FrontendPort.Value == 0)
            {
                return BackendUrl; // Fall back to backend URL if no frontend port specified
            }

            if (string.IsNullOrWhiteSpace(ServerUrl))
            {
                return ServerUrl;
            }

            try
            {
                Uri baseUri = new(ServerUrl);
                int defaultPort = baseUri.Scheme == "https" ? 443 : 80;

                // Only include port in URL if it's non-standard
                if (FrontendPort.Value == defaultPort)
                {
                    return baseUri.ToString().TrimEnd('/');
                }

                UriBuilder ub = new(baseUri) { Port = FrontendPort.Value };
                return ub.Uri.ToString().TrimEnd('/');
            }
            catch
            {
                return BackendUrl; // Fall back to backend URL on error
            }
        }
    }

    public string? IpAddress { get; set; } // Last resolved IPv4/IPv6 string for convenience

    public string? Notes { get; set; }

    public int Backend { get; set; } // Stored as int: cast to PrinterBackend enum (0=Unknown, 1=Moonraker, 2=PrusaLink, 3=SDCP, 4=OctoPrint)

    public string? ApiKey { get; set; } // For PrusaLink/OctoPrint

    public string? CameraStreamUrl { get; set; } // For OctoPrint/Moonraker/PrusaLink

    public string? CameraSnapshotUrl { get; set; } // For OctoPrint/Moonraker/PrusaLink

    public Guid ManufacturerId { get; set; } // No longer nullable - uses default "Unknown" manufacturer

    public Manufacturer? Manufacturer { get; set; }

    public Guid ModelId { get; set; } // No longer nullable - uses default "Unknown Model"

    public PrinterModel? Model { get; set; }

    public Guid? TemplateMachineProfileId { get; set; } // Optional: reference machine profile for custom printers (e.g., CORE One L using CORE One profiles)

    public MachineProfile? TemplateMachineProfile { get; set; }

    public Guid? LocationId { get; set; } // Optional location for organizing printers geographically

    public Location? Location { get; set; }

    public DateTime? DateAcquired { get; set; }

    // Hardware Specifications (previously in PrinterCapabilities)
    public double? MaxBuildVolumeX { get; set; }

    public double? MaxBuildVolumeY { get; set; }

    public double? MaxBuildVolumeZ { get; set; }

    public bool HasHeatedBed { get; set; } = true;

    public bool HasEnclosure { get; set; }

    public bool MultiMaterial { get; set; }

    public bool SupportsAutoLeveling { get; set; }

    public int? MaxPrintSpeed { get; set; }

    // Bed temperature ranges
    public int? MaxBedTemp { get; set; }

    // Material and job tracking
    [ImportExport(ImportExportTargets.Import)]
    public string? CurrentMaterial { get; set; } // From Spoolman integration

    [ImportExport(ImportExportTargets.Import)]
    public int? CurrentSpoolId { get; set; } // Spoolman spool ID

    // Availability
    [ImportExport(ImportExportTargets.Import)]
    public bool IsAvailable { get; set; } = true; // Can accept new jobs

    public DateTime LastCapabilityUpdate { get; set; } = DateTime.UtcNow;

    // Multi-toolhead support (one-to-many with Toolhead)
    /// <summary>
    /// Collection of toolheads (hotends/nozzles) for this printer.
    /// For single-toolhead printers, this will have one entry.
    /// For multi-toolhead printers (Prusa XL, Bambu Lab X1, etc.), this will have multiple entries.
    /// </summary>
    public ICollection<Toolhead> Toolheads { get; set; } = new List<Toolhead>();

    public bool InMaintenance { get; set; } = false;

    public bool IsEnabled { get; set; } = true; // If false, printer is hidden from normal user listings until approved by admin
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

    public string? Url { get; set; }

    public string? Description { get; set; }

    public ICollection<PrinterModel> Models { get; } = new List<PrinterModel>();

    public bool IsActive { get; set; } = true;
}

public class Location
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int PrinterCount { get; set; } = 0; // Denormalized count for efficient filtering

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    // Navigation property: all printers in this location
    public ICollection<Printer> Printers { get; } = new List<Printer>();
}

// Explicit table mapping to ensure EF Core creates the expected "Models" table during test initialization.
[Table("Models")]
public class PrinterModel
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid ManufacturerId { get; set; }

    public Manufacturer? Manufacturer { get; set; }

    public int? MotionType { get; set; } // MotionType enum: 0=Cartesian, 1=CoreXY, 2=Delta, 99=Unknown

    public double? MaxX { get; set; }

    public double? MaxY { get; set; }

    public double? MaxZ { get; set; }

    public int? DefaultBackend { get; set; } // Stored as int: cast to PrinterBackend enum (0=Unknown, 1=Moonraker, 2=PrusaLink, 3=SDCP, 4=OctoPrint)

    public bool HasHeatedBed { get; set; } = true;

    public bool HasEnclosure { get; set; }

    public bool MultiMaterial { get; set; }

    public int NumberOfExtruders { get; set; } = 1;

    public bool SupportsAutoLeveling { get; set; }

    public int? MaxBedTemp { get; set; } = 120;

    public int? MaxPrintSpeed { get; set; } = 150; // mm/s

    public ICollection<PrinterModelFilamentType> SupportedFilamentTypes { get; } = new List<PrinterModelFilamentType>();

    // Toolhead templates for multi-toolhead printers (contains nozzle diameter and max hotend temp)
    public ICollection<PrinterModelToolhead> Toolheads { get; } = new List<PrinterModelToolhead>();

    // Asset URLs for UI display
    public string? CoverImageUrl { get; set; } // URL to printer cover image (from OrcaSlicer assets)

    public string? BedTextureUrl { get; set; } // URL to bed texture image (from OrcaSlicer assets)

    public bool IsActive { get; set; } = true;
}

public class FilamentType
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public double? DefaultHotendTemp { get; set; }

    public double? DefaultBedTemp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PrinterModelFilamentType> PrinterModels { get; } = new List<PrinterModelFilamentType>();

    public bool IsActive { get; set; } = true;
}

public class PrinterModelFilamentType
{
    public Guid PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public Guid FilamentTypeId { get; set; }

    public FilamentType? FilamentType { get; set; }
}

/// <summary>
/// Maps slicer-specific printer model names to canonical PrinterModel entries.
/// For example, PrusaSlicer calls a model "COREONEL" while OrcaSlicer calls it "Prusa CORE One",
/// but both refer to the same physical printer in our catalog.
/// </summary>
public class PrinterModelAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>
    /// The canonical PrinterModel this alias refers to
    /// </summary>
    public Guid PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }
    /// <summary>
    /// The slicer-specific name (e.g., "COREONEL", "Phrozen Arco", "Prusa CORE One")
    /// </summary>
    public string SlicerModelName { get; set; } = string.Empty;
    /// <summary>
    /// The slicer type (e.g., "PrusaSlicer", "OrcaSlicer", "Cura") - optional, if null applies to all slicers
    /// </summary>
    public string? SlicerType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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

/// <summary>
/// Abstract base class for all stored files (GCode and 3D Models).
/// Consolidates common file storage and management properties.
/// </summary>
public abstract class StoredFile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty; // Original filename for display

    public string FileName { get; set; } = string.Empty; // GUID-based filename on disk

    public Guid FolderId { get; set; } // Foreign key to FolderNode entity - REQUIRED

    public FolderNode? Folder { get; set; } // Navigation property to FolderNode

    public string FilePath { get; set; } = string.Empty; // Directory path where file is stored

    public string? ThumbnailFileName { get; set; } // Just the thumbnail filename (stored in same directory as file)

    public long FileSizeBytes { get; set; }

    public string FileHash { get; set; } = string.Empty; // SHA256 for deduplication

    public DateTime UploadedAt { get; set; }

    public string? Description { get; set; }

    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>(); // Skip-navigation collection for modern EF Core

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // File health status (populated by FileConsistencyAuditService)
    public DateTime? LastHealthCheckDate { get; set; }

    public FileHealthStatus HealthStatus { get; set; } = FileHealthStatus.Unknown;

    public string? LastVerificationResult { get; set; } // JSON object with verification details
}

public class GcodeFile : StoredFile
{
    /// <summary>
    /// File extension/type derived from FileName (e.g., "gcode", "bgcode").
    /// Computed property - not stored in database.
    /// </summary>
    public string FileType => System.IO.Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();

    public GcodeSource Source { get; set; }

    public Guid? SourcePrinterId { get; set; } // Printer it was harvested from

    public Printer? SourcePrinter { get; set; }

    public string? OriginalPrinterPath { get; set; } // Original path on the printer

    public DateTime? LastSeenOnPrinter { get; set; } // Last time this file was seen during harvest

    public double? RequiredNozzleDiameter { get; set; } // e.g., 0.4mm

    public string? RequiredMaterial { get; set; } // e.g., "PLA", "PETG"

    public double? EstimatedPrintTimeMinutes { get; set; }

    public double? EstimatedFilamentLengthMm { get; set; }

    public double? EstimatedFilamentWeightG { get; set; }

    public string? ExtractedPrinterModelName { get; set; } // Raw printer model name extracted from gcode (before resolution to PrinterModelId)

    public Guid? PrinterModelId { get; set; } // Printer model this file was sliced for (resolved from extracted name)

    public PrinterModel? PrinterModel { get; set; }

    public string? SlicerName { get; set; } // e.g., "PrusaSlicer", "Cura"

    public string? SlicerVersion { get; set; }

    public string? PrintSettingsId { get; set; } // Slicer process profile name (e.g., "Standard", "Draft") - different from printer model

    public double? LayerHeight { get; set; }

    public double? InfillPercentage { get; set; }

    public int? Perimeters { get; set; } // Number of perimeter/wall loops

    public double? PrintTemperature { get; set; } // First layer print/hotend temperature

    public double? BedTemperature { get; set; } // First layer bed temperature

    public double? PrintSpeed { get; set; }

    // Navigation property to harvest file mappings
    public ICollection<HarvestFileGcodeFileMapping> HarvestFileMappings { get; set; } = new List<HarvestFileGcodeFileMapping>();
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

    // Enhanced error tracking
    public string? ErrorMessage { get; set; } // User-friendly error message

    public string? ErrorType { get; set; } // ConnectionError, AuthenticationError, FileSystemError, ValidationError, UnknownError

    public string? ErrorPhase { get; set; } // Discovery, Download, Processing, Completion

    public string? ErrorDetails { get; set; } // JSON: { exceptionType, stackTrace, additionalInfo }

    public string? FailedResource { get; set; } // File path or URL that caused the failure

    public bool IsRetryable { get; set; } = false; // Whether this error can be retried

    public DateTime? ErrorOccurredAt { get; set; } // Exact timestamp of error

    // File statistics
    public int FilesFound { get; set; }

    public int FilesAdded { get; set; }

    public int FilesSkipped { get; set; } // Already in library

    public int FilesErrored { get; set; }

    public long TotalBytesProcessed { get; set; }

    // Harvest options
    public bool IncludeSubdirectories { get; set; } = true;

    public long? MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB default

    public DateTime? ModifiedAfter { get; set; } // Only harvest files modified after this date

    public string[]? FileExtensions { get; set; } // JSON stored list of allowed extensions (without dot)

    public long? MinFileSizeBytes { get; set; }

    public string? DuplicateHandling { get; set; }

    // Navigation property: Collection of discovered files in this operation
    // Cascade delete: If operation is deleted, discovered files are deleted (but GcodeFiles are protected by Restrict behavior)
    public ICollection<HarvestDiscoveredFile> DiscoveredFiles { get; set; } = new List<HarvestDiscoveredFile>();
}

public enum GcodeHarvestStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}

public enum HarvestErrorType
{
    ConnectionError = 0,      // Network/connectivity issues
    AuthenticationError = 1,  // API key or permission problems
    FileSystemError = 2,      // Can't access files/directories
    ValidationError = 3,      // File validation failures
    UnknownError = 4          // Unexpected exceptions
}

public enum HarvestErrorPhase
{
    Discovery = 0,    // Failed during file listing
    Download = 1,     // Failed during file download
    Processing = 2,   // Failed during file processing/import
    Completion = 3    // Failed during finalization
}

// Discovered G-code files during harvest (before adding to library)


// Mapping table linking harvest files to the gcode files created from them
// Preserves harvest metadata (slicer, material, nozzle, etc) separate from the library file
public class HarvestFileGcodeFileMapping
{
    public Guid Id { get; set; }

    public Guid HarvestDiscoveredFileId { get; set; }

    public HarvestDiscoveredFile HarvestDiscoveredFile { get; set; } = null!;

    public Guid GcodeFileId { get; set; }

    public GcodeFile GcodeFile { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// 3D Model Management System
public class Model3D : StoredFile
{
    public ModelFileFormat FileFormat { get; set; }

    public double? DimensionX { get; set; } // in mm

    public double? DimensionY { get; set; } // in mm  

    public double? DimensionZ { get; set; } // in mm

    public int? TriangleCount { get; set; }

    public bool IsValid { get; set; } = true;

    public string? ValidationErrors { get; set; } // JSON array of validation issues

    public Guid? UploadedByUserId { get; set; }

    public User? UploadedByUser { get; set; }
}

public enum ModelFileFormat
{
    STL = 0,
    TMF = 1,  // 3MF
    OBJ = 2,
    PLY = 3,
    STEP = 4
}

public enum FileHealthStatus
{
    Unknown = 0,      // Never checked or status unknown
    Healthy = 1,      // File exists, hash and size match
    Missing = 2,      // File not found on disk
    Corrupted = 3,    // File exists but hash/size mismatch
    Inaccessible = 4  // File exists but cannot be read (permission denied)
}

// Slicer Profile Management System

/// <summary>
/// Process/Quality profile from OrcaSlicer.
/// Contains quality/speed settings like layer height, infill density, print speeds, etc.
/// Does NOT contain material or machine settings - those are stored in separate FilamentProfile and MachineProfile entities.
/// </summary>
public class ProcessProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    public Guid? PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public Guid? SpecificPrinterId { get; set; } // Optional: specific printer instance

    public Printer? SpecificPrinter { get; set; }

    public double LayerHeight { get; set; } = 0.2; // in mm

    public int InfillPercentage { get; set; } = 20; // 0-100%

    public double PrintSpeed { get; set; } = 50; // mm/s

    public bool EnableSupports { get; set; }

    public ProfileQuality Quality { get; set; } = ProfileQuality.Standard;

    public string? AdvancedSettings { get; set; } // JSON object with additional slicer-specific settings
    /// <summary>
    /// Version of the slicer this profile is for (e.g., "1.7.0", "2.0.0").
    /// Extracted from the profile metadata during import.
    /// Null indicates version information was not available in the profile.
    /// Used to ensure profiles are only used with compatible slicer versions.
    /// </summary>
    public string? SlicerVersion { get; set; }
    /// <summary>
    /// Raw slicer profile JSON as imported from OrcaSlicer / PrusaSlicer (sanitized but otherwise unchanged).
    /// </summary>
    public string? RawJson { get; set; }
    /// <summary>
    /// Extracted settings as key-value pairs for all properties in the raw JSON.
    /// Used for quick display and NewSliceJob page configuration without parsing full RawJson.
    /// </summary>
    public string? SettingsJson { get; set; }
    /// <summary>
    /// Stable hash (SHA256) of RawJson used for deduplication and quick matching on import.
    /// Different slicer versions will produce different hashes even for the same profile characteristics,
    /// ensuring version-specific profiles are maintained separately in the database.
    /// </summary>
    public string? Hash { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPublic { get; set; } = true; // Can be used by other users
    /// <summary>
    /// Indicates profile shipped by system seeding (immutable for regular users).
    /// System profiles come from the OrcaSlicer worker service and are version-specific.
    /// </summary>
    public bool IsSystem { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Machine/Printer profile from OrcaSlicer.
/// Contains printer-specific configuration like bed size, extruders, etc.
/// Stored separately from process and filament profiles as they have no overlap.
/// </summary>
public class MachineProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    public Guid? PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public string? RawJson { get; set; } // Full profile JSON

    public string? SettingsJson { get; set; } // Extracted settings as key-value pairs

    public string? Hash { get; set; } // SHA256 for deduplication

    public bool IsSystem { get; set; } // From OrcaSlicer system profiles

    public bool IsDefault { get; set; } // Can be set as default machine

    public bool IsPublic { get; set; } = true; // Can be used by other users

    public string? SlicerVersion { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Filament/Material profile from OrcaSlicer.
/// Contains material-specific settings like temperature, speed, etc.
/// Stored separately from machine and process profiles as they have no overlap.
/// </summary>
public class FilamentProfile
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Material { get; set; } = "PLA";

    public string? Manufacturer { get; set; }

    public string? Description { get; set; }

    public SlicerType SlicerType { get; set; }

    public int NozzleTemperature { get; set; } = 210; // °C

    public int BedTemperature { get; set; } = 60; // °C

    public int PrintSpeed { get; set; } = 50; // mm/s

    public string? RawJson { get; set; } // Full profile JSON

    public string? SettingsJson { get; set; } // Extracted settings as key-value pairs

    public string? Hash { get; set; } // SHA256 for deduplication

    public bool IsSystem { get; set; } // From OrcaSlicer system profiles

    public bool IsDefault { get; set; } // Can be set as default filament

    public bool IsPublic { get; set; } = true; // Can be used by other users

    public string? SlicerVersion { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public User? CreatedByUser { get; set; }

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

    public Guid? AssignedPrinterId { get; set; }

    public Printer? AssignedPrinter { get; set; }

    public PrintJobStatus Status { get; set; }

    public int Priority { get; set; } // Higher = more important

    public int QueuePosition { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    public string[]? RequiredCapabilities { get; set; } // JSON array of required capabilities

    public TimeSpan? EstimatedPrintTime { get; set; }

    public double? EstimatedFilamentUsage { get; set; }

    public DateTime? ActualStartTime { get; set; }

    public DateTime? ActualEndTime { get; set; }

    public TimeSpan? ActualPrintTime { get; set; }

    public double? ActualFilamentUsage { get; set; }

    public string? FailureReason { get; set; }

    public Guid[]? PreferredPrinterIds { get; set; } // JSON array of preferred printer IDs

    public Guid[]? ExcludedPrinterIds { get; set; } // JSON array of excluded printer IDs

    public string? Notes { get; set; } // Job notes/comments (max 500 characters)

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime QueuedAt { get; set; }

    // Phase 3C: Timeline tracking
    public ICollection<JobStateHistory> StateHistory { get; } = new List<JobStateHistory>();

    // Phase 4.1: Job Scheduling (one-to-one relationship)
    public JobSchedule? Schedule { get; set; }

    // Phase 4.2: Completion Statistics (one-to-one relationship)
    public PrintJobStatistics? Statistics { get; set; }

    // Phase 4.4: Job Retry History
    /// <summary>
    /// Retry history where THIS job is the original failed job
    /// </summary>
    public ICollection<JobRetry> RetriesAsOriginal { get; } = new List<JobRetry>();

    /// <summary>
    /// Retry history where THIS job is a retry attempt (reference to original in JobRetry.OriginalJobId)
    /// </summary>
    public ICollection<JobRetry> RetriesAsAttempt { get; } = new List<JobRetry>();
}

/// <summary>
/// Tracks state transitions for a print job (Phase 3C)
/// </summary>
public class JobStateHistory
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public PrintJob PrintJob { get; set; } = null!;

    public string FromState { get; set; } = string.Empty; // Previous state

    public string ToState { get; set; } = string.Empty; // New state

    public DateTime TransitionedAtUtc { get; set; }

    public TimeSpan? DurationInState { get; set; } // How long job stayed in FromState

    public string? Notes { get; set; } // Optional notes about the transition

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Records job completion statistics for predictive modeling (Phase 4.2)
/// One-to-one relationship with PrintJob (optional, only filled after job completes)
/// </summary>
public class PrintJobStatistics
{
    public Guid Id { get; set; }

    public Guid PrintJobId { get; set; }

    public PrintJob PrintJob { get; set; } = null!;

    // Duration tracking
    public long? ActualDurationMs { get; set; }        // Actual time taken in milliseconds

    public long? EstimatedDurationMs { get; set; }     // Time from gcode estimate in milliseconds

    // Job characteristics
    public Guid? PrinterModelId { get; set; }

    public PrinterModel? PrinterModel { get; set; }

    public string? Material { get; set; }              // PLA, ABS, PETG, TPU, etc.

    public int? NozzleTemperature { get; set; }        // Celsius

    public int? BedTemperature { get; set; }           // Celsius

    public int SpeedPercentage { get; set; } = 100;    // % of normal speed

    // Outcome
    public bool IsSuccess { get; set; }

    public string? FailureReason { get; set; }         // Why it failed if IsSuccess=false

    public DateTime? CompletedAtUtc { get; set; }

    // Audit
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
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

    // Account lockout fields
    public int FailedLoginAttempts { get; set; } = 0;

    public DateTime? LockoutEnd { get; set; }

    public DateTime? LastFailedLogin { get; set; }

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

public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsRevoked { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? RevokedByIp { get; set; }

    public string? ReplacedByToken { get; set; } // Token that replaced this one during refresh

    public string CreatedByIp { get; set; } = string.Empty;
}

public class PasswordResetToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public string Token { get; set; } = string.Empty; // URL-safe token (base64 or GUID)

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; } // Typically 1 hour expiration

    public bool IsUsed { get; set; }

    public DateTime? UsedAt { get; set; }

    public string? UsedByIp { get; set; }
}

public class AuthAuditLog
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; } // Nullable for failed login attempts where user doesn't exist

    public User? User { get; set; }

    public AuthEventType EventType { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public string? Metadata { get; set; } // JSON for additional context (e.g., email for forgot password, lockout duration, etc.)

    public string? CorrelationId { get; set; } // For request tracing
}

public class RevokedToken
{
    public Guid Id { get; set; }

    public string TokenHash { get; set; } = string.Empty; // SHA256 hash of JWT token (for privacy/storage efficiency)

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public DateTime RevokedAt { get; set; } = DateTime.UtcNow;

    public Guid? RevokedByUserId { get; set; } // Admin who revoked the token

    public User? RevokedByUser { get; set; }

    public string Reason { get; set; } = string.Empty; // Reason for revocation (e.g., "Security breach", "User request", "Admin action")

    public DateTime ExpiresAt { get; set; } // Original token expiration (for cleanup purposes)

    public string? IpAddress { get; set; } // IP from which revocation was initiated
}

public enum AuthEventType
{
    None = 0,
    Login = 1,
    LoginFailed = 2,
    Logout = 3,
    Register = 4,
    PasswordChange = 5,
    PasswordReset = 6,
    PasswordResetInitiated = 7,
    AccountLocked = 8,
    AccountUnlocked = 9,
    RefreshToken = 10,
    TokenRevoked = 11
}

public class SystemLog
{
    public int Id { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string Level { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Exception { get; set; }

    public string? Source { get; set; }

    public string? CorrelationId { get; set; } // For end-to-end tracing

    public string? Metadata { get; set; } // JSON metadata for arbitrary context
}


public class HarvestDiscoveredFile
{
    [Key]
    public Guid Id { get; set; }

    public Guid HarvestOperationId { get; set; }

    public GcodeHarvestOperation? HarvestOperation { get; set; } // Navigation property to parent operation

    public string FilePath { get; set; } = string.Empty; // Path on printer

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ThumbnailUrl { get; set; }

    public HarvestFileStatus Status { get; set; } = HarvestFileStatus.Pending;

    public string? Error { get; set; }

    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool AlreadyInLibrary { get; set; } = false;

    public string? FileHash { get; set; }

    public double? ExtractedNozzleDiameter { get; set; }

    public string? ExtractedMaterial { get; set; }

    public double? ExtractedPrintTime { get; set; }

    public double? ExtractedFilamentLength { get; set; }

    public string? ExtractedSlicerName { get; set; }

    public string? ExtractedSlicerVersion { get; set; }

    public DateTime? ModifiedAt { get; set; }

    // Navigation property to harvest file to gcode file mappings
    // Protected by Restrict delete behavior - prevents accidental deletion when cleaning up harvest operations
    public ICollection<HarvestFileGcodeFileMapping> GcodeFileMappings { get; set; } = new List<HarvestFileGcodeFileMapping>();
}

public enum HarvestFileStatus
{
    Pending = 0,
    InProgress = 1,
    Complete = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5
}

public class SlicerSettings
{
    public int Id { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public string? PerEngineJson { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public double JitterPercent { get; set; } = 15.0;
}

// File Consistency Audit System
public class FileHealthAudit
{
    public Guid Id { get; set; }

    public DateTime AuditDate { get; set; }

    public FileAuditType AuditType { get; set; } // Model3D, GcodeFile, or Orphaned

    // Statistics
    public int FilesChecked { get; set; }

    public int HealthyFiles { get; set; }

    public int MissingFiles { get; set; }

    public int CorruptedFiles { get; set; }

    public int OrphanedFiles { get; set; }

    // Details - JSON arrays of file IDs/paths with issues
    public string? MissingFileIds { get; set; } // JSON array of Guids

    public string? CorruptedFileIds { get; set; } // JSON array of Guids

    public string? OrphanedFilePaths { get; set; } // JSON array of file paths

    // Summary & status
    public string? SummaryMessage { get; set; } // Human-readable audit summary

    public bool HasIssues { get; set; } // True if any files missing/corrupted/orphaned

    public DateTime CreatedAt { get; set; }
}

public enum FileAuditType
{
    Model3D = 0,
    GcodeFile = 1,
    OrphanedFiles = 2,
    FullAudit = 3
}

/// <summary>
/// Represents a virtual folder for organizing 3D models and G-code files.
/// Folders provide hierarchical organization and enable referential integrity through FK relationships.
/// Each folder is associated with a specific content type (models or gcode).
/// </summary>
public class FolderNode
{
    public Guid Id { get; set; }

    public string Path { get; set; } = string.Empty; // Virtual folder path (e.g., "/", "/subfolder")

    public string FolderType { get; set; } = string.Empty; // "models" or "gcode" - specifies which files this folder contains

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeletedAt { get; set; } // Soft delete support

    // Navigation properties to files in this folder
    public ICollection<Model3D> Models { get; set; } = new List<Model3D>();

    public ICollection<GcodeFile> Files { get; set; } = new List<GcodeFile>();
}

[SuppressMessage("Naming", "CA1724:Type names should not match namespace", Justification = "Renamed infra domain type to PasswordPolicyEntity to avoid CA1724 conflicts with API domain type.")]
public class PasswordPolicyEntity
{
    public int Id { get; set; }

    public int MinLength { get; set; } = 8;

    public bool RequireUppercase { get; set; }

    public bool RequireLowercase { get; set; }

    public bool RequireDigit { get; set; }

    public bool RequireSymbol { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Generic tag that can be applied to any taggable object (Model3D, GcodeFile, Printer, etc.)
/// </summary>
public class Tag
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty; // e.g., "functional", "decorative", "tools"

    public string? Color { get; set; } // Optional hex color for UI display (e.g., "#FF5733")

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>/// Queue item for G-code harvest operations. Decouples the API request from the background processing.
/// Allows multiple harvest requests to be queued and processed sequentially or with priority.
/// </summary>
public class GcodeHarvestQueueItem
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? ProcessingStartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int Priority { get; set; } = 0; // Higher = process sooner

    /// <summary>
    /// Current status of the queue item.
    /// </summary>
    public GcodeHarvestQueueItemStatus Status { get; set; } = GcodeHarvestQueueItemStatus.Pending;

    /// <summary>
    /// Serialized StartGcodeHarvestDto parameters as JSON for deferred processing.
    /// </summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>
    /// Error message if processing failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Error details for debugging (stack trace, additional context).
    /// </summary>
    public string? ErrorDetails { get; set; }

    // Results cached after completion
    public int FilesFound { get; set; }

    public int FilesAdded { get; set; }

    public int FilesSkipped { get; set; }

    public int FilesErrored { get; set; }

    // Navigation
    public Printer? Printer { get; set; }
}

/// <summary>
/// Status of a harvest operation in the queue.
/// </summary>
public enum GcodeHarvestQueueItemStatus
{
    Pending = 0,      // Waiting to be processed
    Processing = 1,   // Currently being processed
    Completed = 2,    // Successfully completed
    Failed = 3,       // Failed during processing
    Cancelled = 4     // Cancelled by user
}

/// <summary>
/// Phase 4.1: Job Scheduling
/// Represents scheduling configuration for a print job.
/// Separate table to keep PrintJob clean (only for scheduled jobs, not on-demand).
/// One-to-one relationship with PrintJob.
/// </summary>
public class JobSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to PrintJob
    /// </summary>
    public Guid PrintJobId { get; set; }

    public PrintJob PrintJob { get; set; } = null!;

    /// <summary>
    /// Scheduled start time in UTC
    /// </summary>
    public DateTime ScheduledStartTime { get; set; }

    /// <summary>
    /// Timezone for display/input (e.g., "America/New_York", "UTC")
    /// </summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// Recurrence pattern if job should repeat (null = one-time)
    /// Values: "Daily", "Weekly", "Monthly", null
    /// </summary>
    public string? RecurrencePattern { get; set; }

    /// <summary>
    /// When recurrence should end (null = indefinite for recurring jobs)
    /// </summary>
    public DateTime? RecurrenceEndDate { get; set; }

    /// <summary>
    /// Is this scheduled job currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Is this scheduled job paused (can be resumed)
    /// </summary>
    public bool IsPaused { get; set; } = false;

    /// <summary>
    /// When the job was originally scheduled
    /// </summary>
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property to execution history (for recurring jobs)
    /// </summary>
    public ICollection<JobExecution> Executions { get; set; } = new List<JobExecution>();
}

/// <summary>
/// Phase 4.1: Job Execution Tracking
/// Tracks execution history for scheduled jobs (especially recurring ones)
/// </summary>
public class JobExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key to JobSchedule
    /// </summary>
    public Guid JobScheduleId { get; set; }

    public JobSchedule JobSchedule { get; set; } = null!;

    /// <summary>
    /// When this execution was scheduled to run
    /// </summary>
    public DateTime ScheduledExecutionTime { get; set; }

    /// <summary>
    /// When this execution actually started (null if not started yet)
    /// </summary>
    public DateTime? ActualStartTime { get; set; }

    /// <summary>
    /// Execution status: Pending, Running, Completed, Failed, Cancelled
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Result message or error details
    /// </summary>
    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Retry policy configuration for failed print jobs
/// Controls automatic retry behavior with exponential backoff
/// </summary>
public class RetryPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Enable automatic retry on job failure
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts (not counting original attempt)
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial delay in seconds before first retry (e.g., 60 = 1 minute)
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Exponential backoff multiplier (e.g., 2.0 = delay doubles each retry)
    /// Attempt 1: 60s, Attempt 2: 120s, Attempt 3: 240s, Attempt 4: 480s
    /// </summary>
    public double ExponentialBase { get; set; } = 2.0;

    /// <summary>
    /// Maximum delay cap in seconds (prevents infinite backoff growth)
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 3600; // 1 hour

    /// <summary>
    /// Categories of errors that should trigger automatic retry
    /// </summary>
    public string RetryOnErrorCategories { get; set; } = "Recoverable"; // Comma-separated: "Recoverable,Unknown"

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Calculate delay in seconds for a given retry attempt number (1-based)
    /// </summary>
    public int GetDelaySeconds(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            return 0;
        }

        var delaySeconds = (int)Math.Min(
            InitialDelaySeconds * Math.Pow(ExponentialBase, attemptNumber - 1),
            MaxDelaySeconds
        );

        return Math.Max(delaySeconds, InitialDelaySeconds); // Never return less than initial delay
    }
}

/// <summary>
/// Error category classification for retry logic
/// </summary>
public enum ErrorCategory
{
    /// <summary>
    /// Unknown error category - needs manual investigation (default)
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Network timeouts, printer offline, temporary printer errors - should retry
    /// </summary>
    Recoverable = 1,

    /// <summary>
    /// Invalid gcode file, unsupported printer, hardware failure - don't retry
    /// </summary>
    Permanent = 2
}

/// <summary>
/// Retry history for failed print jobs
/// Tracks original failure, retry attempts, and outcomes
/// </summary>
public class JobRetry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Original job ID that failed
    /// </summary>
    public Guid OriginalJobId { get; set; }

    /// <summary>
    /// Navigation property to original print job
    /// </summary>
    public virtual PrintJob? OriginalJob { get; set; }

    /// <summary>
    /// New job ID created for this retry attempt
    /// </summary>
    public Guid RetryJobId { get; set; }

    /// <summary>
    /// Navigation property to retry print job
    /// </summary>
    public virtual PrintJob? RetryJob { get; set; }

    /// <summary>
    /// Attempt number (1 = first retry, 2 = second retry, etc.)
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>
    /// Category of the original failure
    /// </summary>
    public ErrorCategory ErrorCategory { get; set; }

    /// <summary>
    /// Detailed failure reason from the printer/system
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// When the retry was scheduled to begin
    /// </summary>
    public DateTime ScheduledRetryTime { get; set; }

    /// <summary>
    /// When the retry actually started
    /// </summary>
    public DateTime? ActualRetryTime { get; set; }

    /// <summary>
    /// Status: Pending, Running, Succeeded, Failed
    /// </summary>
    public string Status { get; set; } = "Pending"; // Pending, Running, Succeeded, Failed

    /// <summary>
    /// Additional notes about the retry attempt
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
