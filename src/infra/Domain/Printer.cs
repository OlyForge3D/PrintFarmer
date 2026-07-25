using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Domain;

public class Printer
{
    public Guid Id { get; set; }

    /// <summary>
    /// Optimistic concurrency token for EF Core.
    /// Automatically updated on each modification to detect concurrent edits.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public string Name { get; set; } = string.Empty;

    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use ServerUri for typed access")]
    [JsonIgnore]
    public string ServerUrl { get; set; } = string.Empty; // e.g., http://printer:7125 or PrusaLink base URL (IP-resolved)

    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Persisted as text for EF/DTO; use OriginalServerUri for typed access")]
    [JsonIgnore]
    public string? OriginalServerUrl { get; set; } // Original URL/host (for re-resolving if IP changes)

    [JsonIgnore]
    public int BackendPort { get; set; } // Port for backend connection: 7125 (Moonraker), 80 (PrusaLink/OctoPrint/SDCP). ALWAYS SET BY DISCOVERY PROBES - NEVER DEFAULT!

    [JsonIgnore]
    public int? FrontendPort { get; set; } // null for non-Moonraker, 80 for Moonraker by default

    [NotMapped]
    [JsonIgnore]
    public Uri? ServerUri
    {
        get => Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri? u) ? u : null;
        set => ServerUrl = value?.ToString() ?? string.Empty;
    }

    [NotMapped]
    [JsonIgnore]
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
    [JsonIgnore]
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
    [JsonIgnore]
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

    public string? Notes { get; set; }

    public int Backend { get; set; } // Stored as int: cast to PrinterBackend enum (0=Unknown, 1=Moonraker, 2=PrusaLink, 3=SDCP, 4=OctoPrint)

    /// <summary>
    /// Application-managed revision of calibration-relevant configuration.
    /// Transient status updates do not change this value.
    /// </summary>
    public long ConfigurationRevision { get; set; } = 1;

    /// <summary>When calibration-relevant configuration last changed.</summary>
    public DateTime? CalibrationConfigurationUpdatedAtUtc { get; set; }

    /// <summary>Explicit firmware family; never inferred from backend or catalog metadata.</summary>
    public PrinterFirmwareFamily FirmwareFamily { get; set; } = PrinterFirmwareFamily.Unknown;

    /// <summary>Explicit G-code dialect; never inferred from backend or catalog metadata.</summary>
    public PrinterGcodeDialect GcodeDialect { get; set; } = PrinterGcodeDialect.Unknown;

    /// <summary>Authoritative source that supplied the firmware identity.</summary>
    public FirmwareDetectionSource FirmwareDetectionSource { get; set; } = FirmwareDetectionSource.Unknown;

    /// <summary>Observed or configured firmware version.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>Version of the detector or configuration contract that asserted firmware identity.</summary>
    public string? FirmwareDetectionVersion { get; set; }

    /// <summary>Detection confidence from zero to one, when supplied by the authoritative source.</summary>
    public decimal? FirmwareDetectionConfidence { get; set; }

    /// <summary>When firmware identity was observed or explicitly configured.</summary>
    public DateTime? FirmwareDetectedAtUtc { get; set; }

    /// <summary>Whether the firmware identity has been explicitly verified.</summary>
    public bool FirmwareIdentityVerified { get; set; }

    /// <summary>Backend implementation version captured without connection details.</summary>
    public string? BackendVersion { get; set; }

    /// <summary>Backend API version captured without connection details.</summary>
    public string? BackendApiVersion { get; set; }

    [JsonIgnore]
    public string? ApiKey { get; set; } // For PrusaLink/OctoPrint

    /// <summary>
    /// Username for HTTP Digest Authentication (used by PrusaLink for privileged API access).
    /// Combined with Password, enables access to additional endpoints not available with API key alone.
    /// </summary>
    [JsonIgnore]
    public string? Username { get; set; }

    /// <summary>
    /// Password for HTTP Digest Authentication (used by PrusaLink for privileged API access).
    /// Stored encrypted in the database. Combined with Username for full API access.
    /// </summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>
    /// Transient (non-persisted) credential container for backend API access.
    /// Populated by PrintersService after loading the printer from the database.
    /// Contains ApiKey, Username, and Password - backend clients use whatever they need.
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    public PrinterCredential? Credential { get; set; }

    /// <summary>
    /// Cameras attached to this printer.
    /// Cameras are discovered from Moonraker, PrusaLink, OctoPrint, etc. or manually configured.
    /// </summary>
    [JsonIgnore]
    public ICollection<Camera> Cameras { get; set; } = new List<Camera>();

    public Guid ManufacturerId { get; set; } // No longer nullable - uses default "Unknown" manufacturer

    public Manufacturer? Manufacturer { get; set; }

    public Guid ModelId { get; set; } // No longer nullable - uses default "Unknown Model"

    public PrinterModel? Model { get; set; }

    public Guid? TemplateMachineProfileId { get; set; } // Optional: reference machine profile for custom printers (e.g., CORE One L using CORE One profiles)

    // Note: MachineProfile navigation removed — MachineProfile is now in Farm.Slicer.Module.Domain.
    // The relationship is maintained via TemplateMachineProfileId (soft reference).
    public Guid? LocationId { get; set; } // Optional location for organizing printers geographically

    public Location? Location { get; set; }

    public Guid? PrinterGroupId { get; set; } // Optional group of identical printers for dispatch targeting

    public PrinterGroup? PrinterGroup { get; set; }

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

    /// <summary>Explicit calibration bed-origin X coordinate in millimeters.</summary>
    public double? BedOriginX { get; set; }

    /// <summary>Explicit calibration bed-origin Y coordinate in millimeters.</summary>
    public double? BedOriginY { get; set; }

    /// <summary>Exact printable polygon JSON supplied by an authoritative configuration source.</summary>
    public string? PrintablePolygonJson { get; set; }

    /// <summary>Exact excluded-region JSON supplied by an authoritative configuration source.</summary>
    public string? ExcludedRegionsJson { get; set; }

    /// <summary>Explicit motion system for calibration; null means unknown.</summary>
    public CalibrationMotionType? CalibrationMotionType { get; set; }

    /// <summary>Maximum travel speed in millimeters per second.</summary>
    public int? MaxTravelSpeed { get; set; }

    /// <summary>Maximum print acceleration in millimeters per second squared.</summary>
    public int? MaxAcceleration { get; set; }

    /// <summary>Maximum travel acceleration in millimeters per second squared.</summary>
    public int? MaxTravelAcceleration { get; set; }

    /// <summary>
    /// Explicit heated-bed value for calibration. This is separate from legacy catalog defaults.
    /// </summary>
    public bool? CalibrationHasHeatedBed { get; set; }

    /// <summary>
    /// Explicit enclosure value for calibration. This is separate from legacy catalog defaults.
    /// </summary>
    public bool? CalibrationHasEnclosure { get; set; }

    /// <summary>Whether the printer has an actively heated chamber.</summary>
    public bool? HasHeatedChamber { get; set; }

    /// <summary>Maximum chamber temperature in degrees Celsius.</summary>
    public int? MaxChamberTemp { get; set; }

    /// <summary>Zero-based active physical toolhead index.</summary>
    public int? ActiveToolheadIndex { get; set; }

    /// <summary>Whether Klipper pressure-advance semantics were verified.</summary>
    public bool? SupportsPressureAdvance { get; set; }

    /// <summary>Whether Klipper firmware-retraction semantics were verified.</summary>
    public bool? SupportsFirmwareRetraction { get; set; }

    /// <summary>When the complete calibration hardware metadata was verified.</summary>
    public DateTime? CalibrationHardwareVerifiedAtUtc { get; set; }

    /// <summary>Explicit slicer engine identity selected for calibration.</summary>
    public string? CalibrationSlicerEngine { get; set; }

    /// <summary>Explicit slicer distribution identity selected for calibration.</summary>
    public string? CalibrationSlicerDistribution { get; set; }

    /// <summary>Explicit pinned slicer version selected for calibration.</summary>
    public string? CalibrationSlicerVersion { get; set; }

    /// <summary>Explicit slicer profile-format identity selected for calibration.</summary>
    public string? CalibrationProfileFormat { get; set; }

    /// <summary>Explicit upstream OrcaSlicer machine profile soft reference.</summary>
    public Guid? CalibrationMachineProfileId { get; set; }

    /// <summary>Explicit upstream OrcaSlicer process profile soft reference.</summary>
    public Guid? CalibrationProcessProfileId { get; set; }

    /// <summary>Explicit upstream OrcaSlicer filament profile soft reference.</summary>
    public Guid? CalibrationFilamentProfileId { get; set; }

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

    /// <summary>
    /// Power consumption in watts. Overrides the model's default wattage when set.
    /// </summary>
    public decimal? Wattage { get; set; }

    /// <summary>
    /// Per-printer machine hourly rate override for cost tracking.
    /// If null, uses the default rate from CostTrackingSettings.
    /// </summary>
    public decimal? MachineHourlyRate { get; set; }

    // Multi-toolhead support (one-to-many with Toolhead)

    /// <summary>
    /// Collection of toolheads (hotends/nozzles) for this printer.
    /// For single-toolhead printers, this will have one entry.
    /// For multi-toolhead printers (Prusa XL, etc.), this will have multiple entries.
    /// </summary>
    public ICollection<Toolhead> Toolheads { get; set; } = new List<Toolhead>();

    /// <summary>
    /// Collection of maintenance logs for this printer.
    /// </summary>
    public ICollection<MaintenanceLog> MaintenanceLogs { get; set; } = new List<MaintenanceLog>();

    /// <summary>
    /// Cumulative statistics for this printer (one-to-one relationship).
    /// </summary>
    public PrinterStatistics? Statistics { get; set; }

    /// <summary>
    /// Whether AI-powered Obico failure detection is enabled for this printer.
    /// The app auto-assigns an Obico server — users just opt in/out.
    /// </summary>
    public bool ObicoEnabled { get; set; }

    /// <summary>
    /// Tags assigned to this printer for categorization and filtering.
    /// Uses EF Core skip-navigation (many-to-many without explicit join entity).
    /// </summary>
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();

    public bool InMaintenance { get; set; } = false;

    public bool IsEnabled { get; set; } = true; // If false, printer is hidden from normal user listings until approved by admin

    // AutoDispatch Ready-Gate properties

    /// <summary>
    /// When enabled, the printer transitions to PendingReady after a job completes,
    /// waiting for operator confirmation before dispatching the next queued job.
    /// </summary>
    public bool AutoDispatchEnabled { get; set; }

    /// <summary>
    /// Dispatch-related state (AutoDispatchState, BedPreConfirmed) stored in a separate
    /// table to avoid RowVersion contention between user edits and background dispatch writes.
    /// </summary>
    public PrinterDispatchState? DispatchState { get; set; }

    /// <summary>
    /// Background-service-managed state (LastHistorySeedUtc, LastModelSyncAt, LastCapabilityUpdate, ObicoServerId)
    /// stored in a separate table to avoid RowVersion contention when background services write timestamps
    /// while users edit printer config.
    /// </summary>
    public PrinterServiceState? ServiceState { get; set; }
}
