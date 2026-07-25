using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// The single compatibility tuple this generator supports, expressed as constants.
/// </summary>
/// <remarks>
/// The tuple is conjunctive and fail closed. Every element must match exactly; nothing is inferred
/// from manufacturer, printer model, backend, aliases, or a Moonraker/OctoPrint response.
/// </remarks>
public static class CalibrationSupportedTuple
{
    /// <summary>The only supported firmware family.</summary>
    public const string FirmwareFamily = "Klipper";

    /// <summary>The only supported G-code dialect.</summary>
    public const string GcodeDialect = "Klipper";

    /// <summary>The only supported slicer engine.</summary>
    public const string SlicerEngine = CalibrationContractConstants.SlicerEngine;

    /// <summary>The only supported slicer distribution.</summary>
    public const string SlicerDistribution = CalibrationContractConstants.SlicerDistribution;

    /// <summary>The pinned upstream slicer version.</summary>
    public const string SlicerVersion = CalibrationContractConstants.SlicerVersion;

    /// <summary>The only supported native profile format.</summary>
    public const string ProfileFormat = CalibrationContractConstants.ProfileFormat;
}

/// <summary>
/// The exact, authoritative compatibility identity supplied by the resolver or attempt snapshot.
/// </summary>
/// <param name="FirmwareFamily">Firmware family reported by the authoritative printer snapshot.</param>
/// <param name="GcodeDialect">G-code dialect reported by the authoritative printer snapshot.</param>
/// <param name="SlicerEngine">Slicer engine recorded on the authoritative snapshot.</param>
/// <param name="SlicerDistribution">Slicer distribution recorded on the authoritative snapshot.</param>
/// <param name="SlicerVersion">Pinned slicer version recorded on the authoritative snapshot.</param>
/// <param name="SlicerContainerDigest">Authoritative container digest of the pinned slicer image.</param>
/// <param name="SlicerBinarySha256">Authoritative digest of the pinned slicer binary.</param>
/// <param name="ProfileFormat">Native profile format recorded on the authoritative snapshot.</param>
public sealed record CalibrationCompatibilityIdentity(
    string? FirmwareFamily,
    string? GcodeDialect,
    string? SlicerEngine,
    string? SlicerDistribution,
    string? SlicerVersion,
    string? SlicerContainerDigest,
    string? SlicerBinarySha256,
    string? ProfileFormat);

/// <summary>How the firmware identity was established, and whether it was verified.</summary>
/// <param name="Family">Firmware family, for example <c>Klipper</c>.</param>
/// <param name="Version">Firmware version string reported by the authoritative source.</param>
/// <param name="DetectionSource">Detection source, for example <c>printer</c> or <c>configured</c>.</param>
/// <param name="GcodeDialect">The G-code dialect asserted for this firmware.</param>
/// <param name="Verified">Whether the identity was verified rather than assumed.</param>
/// <param name="DetectedAtUtc">When the identity was last established.</param>
public sealed record CalibrationFirmwareContext(
    string? Family,
    string? Version,
    string? DetectionSource,
    string? GcodeDialect,
    bool Verified,
    DateTime? DetectedAtUtc);

/// <summary>The toolhead and nozzle a calibration runs on.</summary>
/// <param name="Id">Authoritative toolhead identifier.</param>
/// <param name="Index">Zero-based physical toolhead index.</param>
/// <param name="NozzleDiameterMillimeters">Installed nozzle diameter, in millimetres.</param>
/// <param name="NozzleType">Nozzle type name recorded on the printer configuration.</param>
/// <param name="NozzleMaterial">Nozzle material recorded on the printer configuration.</param>
/// <param name="NozzleMaxTemperatureCelsius">Nozzle temperature ceiling, in degrees Celsius.</param>
/// <param name="HotendMaxTemperatureCelsius">Hotend temperature ceiling, in degrees Celsius.</param>
/// <param name="MaxVolumetricFlow">Toolhead volumetric flow ceiling, in mm³/s.</param>
/// <param name="IsDirectDrive">Whether the toolhead is direct drive.</param>
public sealed record CalibrationToolheadContext(
    Guid Id,
    int Index,
    decimal NozzleDiameterMillimeters,
    string? NozzleType,
    string? NozzleMaterial,
    int? NozzleMaxTemperatureCelsius,
    int? HotendMaxTemperatureCelsius,
    decimal? MaxVolumetricFlow,
    bool? IsDirectDrive);

/// <summary>A bed coordinate in printer space, in millimetres.</summary>
/// <param name="X">X coordinate, in millimetres.</param>
/// <param name="Y">Y coordinate, in millimetres.</param>
public sealed record CalibrationBedPoint(decimal X, decimal Y);

/// <summary>A named region the generator must never enter.</summary>
/// <param name="Name">Operator-facing region name.</param>
/// <param name="Polygon">Closed polygon vertices, in millimetres, in authored order.</param>
public sealed record CalibrationExcludedRegion(
    string Name,
    IReadOnlyList<CalibrationBedPoint> Polygon);

/// <summary>Authoritative build volume, origin, printable polygon and exclusions.</summary>
/// <param name="SizeXMillimeters">Build volume X extent, in millimetres.</param>
/// <param name="SizeYMillimeters">Build volume Y extent, in millimetres.</param>
/// <param name="SizeZMillimeters">Build volume Z extent, in millimetres.</param>
/// <param name="OriginXMillimeters">Bed origin X offset, in millimetres.</param>
/// <param name="OriginYMillimeters">Bed origin Y offset, in millimetres.</param>
/// <param name="PrintablePolygon">Authoritative printable polygon, in millimetres.</param>
/// <param name="ExcludedRegions">Authoritative excluded regions, in millimetres.</param>
public sealed record CalibrationBedGeometry(
    decimal? SizeXMillimeters,
    decimal? SizeYMillimeters,
    decimal? SizeZMillimeters,
    decimal? OriginXMillimeters,
    decimal? OriginYMillimeters,
    IReadOnlyList<CalibrationBedPoint> PrintablePolygon,
    IReadOnlyList<CalibrationExcludedRegion> ExcludedRegions);

/// <summary>Authoritative machine motion and thermal ceilings.</summary>
/// <param name="MaxBedTemperatureCelsius">Bed temperature ceiling.</param>
/// <param name="HasHeatedChamber">Whether a heated chamber exists.</param>
/// <param name="MaxChamberTemperatureCelsius">Chamber temperature ceiling when a chamber exists.</param>
/// <param name="MaxPrintSpeedMillimetersPerSecond">Print speed ceiling.</param>
/// <param name="MaxTravelSpeedMillimetersPerSecond">Travel speed ceiling.</param>
/// <param name="MaxAcceleration">Print acceleration ceiling, in mm/s².</param>
/// <param name="MaxTravelAcceleration">Travel acceleration ceiling, in mm/s².</param>
public sealed record CalibrationMachineLimits(
    int? MaxBedTemperatureCelsius,
    bool? HasHeatedChamber,
    int? MaxChamberTemperatureCelsius,
    int? MaxPrintSpeedMillimetersPerSecond,
    int? MaxTravelSpeedMillimetersPerSecond,
    int? MaxAcceleration,
    int? MaxTravelAcceleration);

/// <summary>The filament product and optional physical spool a calibration runs with.</summary>
/// <param name="ProductProfileId">Authoritative filament profile identifier.</param>
/// <param name="Material">Filament material name.</param>
/// <param name="Sku">Filament SKU recorded on the authoritative snapshot.</param>
/// <param name="Manufacturer">Filament manufacturer recorded on the authoritative snapshot.</param>
/// <param name="DiameterMillimeters">Filament diameter, in millimetres.</param>
/// <param name="NozzleTemperatureCelsius">Baseline nozzle temperature.</param>
/// <param name="BedTemperatureCelsius">Baseline bed temperature.</param>
/// <param name="ChamberTemperatureCelsius">Baseline chamber temperature, when a chamber exists.</param>
/// <param name="FlowRatio">Baseline flow ratio.</param>
/// <param name="MaxVolumetricFlow">Baseline maximum volumetric flow, in mm³/s.</param>
/// <param name="SpoolId">Optional physical spool identifier.</param>
/// <param name="SpoolSnapshotSha256">Optional physical spool snapshot digest.</param>
public sealed record CalibrationFilamentContext(
    Guid ProductProfileId,
    string? Material,
    string? Sku,
    string? Manufacturer,
    decimal? DiameterMillimeters,
    int? NozzleTemperatureCelsius,
    int? BedTemperatureCelsius,
    int? ChamberTemperatureCelsius,
    decimal? FlowRatio,
    decimal? MaxVolumetricFlow,
    Guid? SpoolId,
    string? SpoolSnapshotSha256);

/// <summary>The baseline process values a calibration derives its defaults from.</summary>
/// <param name="LayerHeightMillimeters">Baseline layer height.</param>
/// <param name="FirstLayerHeightMillimeters">Baseline first layer height.</param>
/// <param name="LineWidthMillimeters">Baseline extrusion width.</param>
/// <param name="PrintSpeedMillimetersPerSecond">Baseline print speed.</param>
/// <param name="FirstLayerSpeedMillimetersPerSecond">Baseline first layer speed.</param>
/// <param name="TravelSpeedMillimetersPerSecond">Baseline travel speed.</param>
/// <param name="AccelerationMillimetersPerSecondSquared">Baseline print acceleration.</param>
/// <param name="PressureAdvance">Baseline pressure advance, in seconds.</param>
/// <param name="RetractionLengthMillimeters">Baseline retraction length.</param>
/// <param name="RetractionSpeedMillimetersPerSecond">Baseline retraction speed.</param>
public sealed record CalibrationProcessContext(
    decimal? LayerHeightMillimeters,
    decimal? FirstLayerHeightMillimeters,
    decimal? LineWidthMillimeters,
    int? PrintSpeedMillimetersPerSecond,
    int? FirstLayerSpeedMillimetersPerSecond,
    int? TravelSpeedMillimetersPerSecond,
    int? AccelerationMillimetersPerSecondSquared,
    decimal? PressureAdvance,
    decimal? RetractionLengthMillimeters,
    int? RetractionSpeedMillimetersPerSecond);

/// <summary>An exact native upstream-Orca profile document with its authoritative digest.</summary>
/// <param name="Id">Authoritative profile identifier.</param>
/// <param name="Kind">Profile kind: <c>machine</c>, <c>process</c> or <c>filament</c>.</param>
/// <param name="Name">Profile name as stored.</param>
/// <param name="Revision">Profile revision recorded on the authoritative snapshot.</param>
/// <param name="ExactJson">The verbatim native JSON document; never a serialized CLR DTO.</param>
/// <param name="Sha256">The authoritative digest recorded with the document.</param>
public sealed record CalibrationExactProfile(
    Guid Id,
    string Kind,
    string? Name,
    string? Revision,
    string? ExactJson,
    string? Sha256);

/// <summary>The machine, process and filament profile triplet used by a calibration.</summary>
/// <param name="Machine">Exact native machine profile.</param>
/// <param name="Process">Exact native process profile.</param>
/// <param name="Filament">Exact native filament profile.</param>
public sealed record CalibrationProfileTriplet(
    CalibrationExactProfile? Machine,
    CalibrationExactProfile? Process,
    CalibrationExactProfile? Filament);

/// <summary>The generator identity stamped onto every specification, plan and manifest.</summary>
/// <param name="Name">Stable generator name.</param>
/// <param name="Version">Stable generator version.</param>
public sealed record CalibrationGeneratorIdentity(string Name, string Version)
{
    /// <summary>The generator identity produced by this build.</summary>
    public static CalibrationGeneratorIdentity Current { get; } =
        new("printfarmer.calibration-generator", "1.0.0");
}

/// <summary>
/// A linked imported asset (or normal model) referenced by a calibration method.
/// </summary>
/// <param name="Model3DId">Authoritative stored model identity.</param>
/// <param name="Sha256">Authoritative content digest of the stored model.</param>
/// <param name="Format">Canonical format token: <c>stl</c> or <c>3mf</c>.</param>
/// <param name="SafeFileName">Sanitized display file name; never a path.</param>
/// <param name="SizeBytes">Stored size, in bytes.</param>
/// <param name="Provenance">Provenance token, for example <c>imported</c> or <c>generated</c>.</param>
/// <remarks>
/// This reference deliberately carries no path, URL, or worker-local location. Bytes are supplied by
/// an authorized storage reader chosen by the caller, never by this service.
/// </remarks>
public sealed record CalibrationModelReference(
    Guid Model3DId,
    string? Sha256,
    string? Format,
    string? SafeFileName,
    long SizeBytes,
    string? Provenance);

/// <summary>
/// The complete authoritative context a specification is compiled from.
/// </summary>
/// <remarks>
/// Every member must come from an authoritative resolver, attempt or snapshot. The compiler never
/// synthesizes a missing identity, geometry, nozzle, limit, profile, timestamp or freshness value.
/// </remarks>
public sealed record CalibrationGenerationContext
{
    /// <summary>Gets the owning calibration project identifier.</summary>
    public required Guid ProjectId { get; init; }

    /// <summary>Gets the immutable attempt identifier.</summary>
    public required Guid AttemptId { get; init; }

    /// <summary>Gets the durable orchestration identifier.</summary>
    public required Guid OrchestrationId { get; init; }

    /// <summary>Gets the printer identifier.</summary>
    public required Guid PrinterId { get; init; }

    /// <summary>Gets the printer configuration snapshot identifier.</summary>
    public required Guid PrinterConfigurationSnapshotId { get; init; }

    /// <summary>Gets the printer configuration revision the snapshot was captured at.</summary>
    public required long PrinterConfigurationRevision { get; init; }

    /// <summary>Gets the digest of the sanitized printer configuration snapshot.</summary>
    public required string? PrinterConfigurationSnapshotSha256 { get; init; }

    /// <summary>Gets the current printer configuration revision, used to detect staleness.</summary>
    public required long CurrentPrinterConfigurationRevision { get; init; }

    /// <summary>Gets when the snapshot was captured.</summary>
    public required DateTime? SnapshotCapturedAtUtc { get; init; }

    /// <summary>Gets the authoritative compatibility identity.</summary>
    public required CalibrationCompatibilityIdentity Compatibility { get; init; }

    /// <summary>Gets the authoritative firmware identity.</summary>
    public required CalibrationFirmwareContext Firmware { get; init; }

    /// <summary>Gets the toolhead and nozzle the calibration runs on.</summary>
    public required CalibrationToolheadContext Toolhead { get; init; }

    /// <summary>Gets the authoritative bed geometry.</summary>
    public required CalibrationBedGeometry Bed { get; init; }

    /// <summary>Gets the authoritative machine limits.</summary>
    public required CalibrationMachineLimits Limits { get; init; }

    /// <summary>Gets the authoritative filament context.</summary>
    public required CalibrationFilamentContext Filament { get; init; }

    /// <summary>Gets the authoritative baseline process values.</summary>
    public required CalibrationProcessContext Process { get; init; }

    /// <summary>Gets the exact native profile triplet.</summary>
    public required CalibrationProfileTriplet Profiles { get; init; }

    /// <summary>Gets the generator identity.</summary>
    public required CalibrationGeneratorIdentity Generator { get; init; }

    /// <summary>Gets the idempotency operation identifier for this generation request.</summary>
    public required string? OperationId { get; init; }

    /// <summary>Gets the linked imported asset, when the selected method requires one.</summary>
    public CalibrationModelReference? ImportedAsset { get; init; }

    /// <summary>Gets the maximum snapshot age accepted before the context is treated as stale.</summary>
    public TimeSpan SnapshotFreshnessWindow { get; init; } = TimeSpan.FromHours(24);
}
