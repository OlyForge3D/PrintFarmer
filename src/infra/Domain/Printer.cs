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
