using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Discovery;

/// <summary>
/// Request payload for creating a new printer entry from discovery data.
/// Extends DiscoveryPrinterInfoDto with catalog references and hardware specs.
/// </summary>
public class CreatePrinterFromDiscoveryDto : DiscoveryPrinterInfoDto
{
    /// <summary>
    /// Reference to existing manufacturer in catalog.
    /// If null and NewManufacturerName is provided, a new manufacturer will be created.
    /// </summary>
    public Guid? ManufacturerId { get; set; }

    /// <summary>
    /// Reference to existing model in catalog.
    /// If null and NewModelName is provided, a new model will be created.
    /// </summary>
    public Guid? ModelId { get; set; }

    /// <summary>
    /// Create new manufacturer with this name if ManufacturerId is not provided.
    /// </summary>
    public string? NewManufacturerName { get; set; }

    /// <summary>
    /// Create new model with this name if ModelId is not provided.
    /// </summary>
    public string? NewModelName { get; set; }

    /// <summary>
    /// Location name to assign printer to during import.
    /// Location must already exist or will be skipped.
    /// </summary>
    public string? LocationName { get; set; }

    /// <summary>
    /// Date the printer was acquired (optional metadata).
    /// </summary>
    public DateTime? DateAcquired { get; set; }

    /// <summary>
    /// Whether this printer is visible to normal users.
    /// false = pending admin approval, hidden from normal users
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Hardware specification fields - populated from exported printer data or discovery
    /// </summary>
    public double? MaxBuildVolumeX { get; set; }

    public double? MaxBuildVolumeY { get; set; }

    public double? MaxBuildVolumeZ { get; set; }

    public bool HasHeatedBed { get; set; } = true;

    public bool HasEnclosure { get; set; } = false;

    public bool MultiMaterial { get; set; } = false;

    public bool SupportsAutoLeveling { get; set; } = false;

    public double? NozzleDiameter { get; set; }

    public string[]? SupportedMaterials { get; set; }

    public int? MaxHotendTemp { get; set; }

    public int? MaxBedTemp { get; set; }

    public string? CurrentMaterial { get; set; }

    public int? CurrentSpoolId { get; set; }

    /// <summary>
    /// Power consumption in watts. Overrides the model's default wattage when set.
    /// </summary>
    public decimal? Wattage { get; set; }

    /// <summary>
    /// Per-printer machine hourly rate override for cost tracking.
    /// </summary>
    public decimal? MachineHourlyRate { get; set; }

    /// <summary>
    /// Toolhead configurations for multi-toolhead printers.
    /// If provided during import, these will be created instead of the default single toolhead.
    /// If null, a default single toolhead will be created.
    /// </summary>
    public List<CreateToolheadDto>? Toolheads { get; set; }

    /// <summary>
    /// Create from discovered printer info with optional catalog metadata.
    /// </summary>
    /// <param name="discovered">The discovered printer information.</param>
    /// <param name="manufacturerId">Optional reference to existing manufacturer in catalog.</param>
    /// <param name="modelId">Optional reference to existing model in catalog.</param>
    /// <param name="newManufacturerName">Optional name for creating a new manufacturer.</param>
    /// <param name="newModelName">Optional name for creating a new model.</param>
    public static CreatePrinterFromDiscoveryDto FromDiscovered(
        DiscoveredPrinterDto discovered,
        Guid? manufacturerId = null,
        Guid? modelId = null,
        string? newManufacturerName = null,
        string? newModelName = null) =>
        new()
        {
            Name = discovered.Name,
            ServerUrl = discovered.ServerUrl,
            OriginalServerUrl = discovered.OriginalServerUrl,
            IpAddress = discovered.IpAddress,
            Backend = discovered.Backend,
            BackendPort = discovered.BackendPort,
            FrontendPort = discovered.FrontendPort,
            CameraStreamUrl = discovered.CameraStreamUrl,
            CameraSnapshotUrl = discovered.CameraSnapshotUrl,
            Manufacturer = discovered.Manufacturer,
            Model = discovered.Model,
            Notes = discovered.Notes,
            ApiKey = discovered.ApiKey,
            DiscoveredAt = discovered.DiscoveredAt,
            IsReachable = discovered.IsReachable,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            NewManufacturerName = newManufacturerName,
            NewModelName = newModelName,
            IsEnabled = true
        };
}
