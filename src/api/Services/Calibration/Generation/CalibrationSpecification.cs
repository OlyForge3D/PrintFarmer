namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// One deterministic band of a calibration sweep, resolved to explicit values, units and geometry.
/// </summary>
/// <param name="Index">Zero-based segment index in emission order.</param>
/// <param name="ParameterName">The tuned parameter, for example <c>nozzle_temperature</c>.</param>
/// <param name="Unit">The explicit unit of <paramref name="Value"/>.</param>
/// <param name="Value">The value applied for the whole band.</param>
/// <param name="StartLayer">First one-based layer of the band.</param>
/// <param name="EndLayer">Last one-based layer of the band, inclusive.</param>
/// <param name="StartZMillimeters">Z height of the first layer of the band.</param>
/// <param name="EndZMillimeters">Z height of the last layer of the band.</param>
public sealed record CalibrationSegmentSpecification(
    int Index,
    string ParameterName,
    string Unit,
    decimal Value,
    int StartLayer,
    int EndLayer,
    decimal StartZMillimeters,
    decimal EndZMillimeters);

/// <summary>The resolved sweep the segment list was derived from.</summary>
/// <param name="ParameterName">The tuned parameter.</param>
/// <param name="Unit">The explicit unit.</param>
/// <param name="Start">First value of the sweep.</param>
/// <param name="End">Last value of the sweep.</param>
/// <param name="Step">Step magnitude between values; zero for single-value methods.</param>
/// <param name="SegmentCount">Number of resolved segments.</param>
public sealed record CalibrationParameterSweep(
    string ParameterName,
    string Unit,
    decimal Start,
    decimal End,
    decimal Step,
    int SegmentCount);

/// <summary>
/// The resolved, bounded print parameters the trusted generator prints every segment with.
/// </summary>
/// <param name="LayerHeightMillimeters">Resolved layer height.</param>
/// <param name="FirstLayerHeightMillimeters">Resolved first layer height.</param>
/// <param name="LineWidthMillimeters">Resolved extrusion width.</param>
/// <param name="FilamentDiameterMillimeters">Resolved filament diameter.</param>
/// <param name="PrintSpeedMillimetersPerSecond">Resolved print speed.</param>
/// <param name="FirstLayerSpeedMillimetersPerSecond">Resolved first layer speed.</param>
/// <param name="TravelSpeedMillimetersPerSecond">Resolved travel speed.</param>
/// <param name="AccelerationMillimetersPerSecondSquared">Resolved print acceleration.</param>
/// <param name="NozzleTemperatureCelsius">Resolved baseline nozzle temperature.</param>
/// <param name="BedTemperatureCelsius">Resolved bed temperature.</param>
/// <param name="ChamberTemperatureCelsius">Resolved chamber temperature, when a chamber exists.</param>
/// <param name="FlowRatio">Resolved baseline flow ratio.</param>
/// <param name="PressureAdvance">Resolved baseline pressure advance.</param>
/// <param name="RetractionLengthMillimeters">Resolved baseline retraction length.</param>
/// <param name="RetractionSpeedMillimetersPerSecond">Resolved baseline retraction speed.</param>
/// <param name="MaxVolumetricFlow">Resolved volumetric flow ceiling.</param>
public sealed record CalibrationPrintParameters(
    decimal LayerHeightMillimeters,
    decimal FirstLayerHeightMillimeters,
    decimal LineWidthMillimeters,
    decimal FilamentDiameterMillimeters,
    int PrintSpeedMillimetersPerSecond,
    int FirstLayerSpeedMillimetersPerSecond,
    int TravelSpeedMillimetersPerSecond,
    int AccelerationMillimetersPerSecondSquared,
    int NozzleTemperatureCelsius,
    int BedTemperatureCelsius,
    int? ChamberTemperatureCelsius,
    decimal FlowRatio,
    decimal PressureAdvance,
    decimal RetractionLengthMillimeters,
    int RetractionSpeedMillimetersPerSecond,
    decimal MaxVolumetricFlow);

/// <summary>The deterministic footprint the trusted generator prints inside.</summary>
/// <param name="CenterXMillimeters">Footprint centre X, in millimetres.</param>
/// <param name="CenterYMillimeters">Footprint centre Y, in millimetres.</param>
/// <param name="SizeXMillimeters">Footprint X extent, in millimetres.</param>
/// <param name="SizeYMillimeters">Footprint Y extent, in millimetres.</param>
public sealed record CalibrationFootprint(
    decimal CenterXMillimeters,
    decimal CenterYMillimeters,
    decimal SizeXMillimeters,
    decimal SizeYMillimeters)
{
    /// <summary>Gets the minimum X coordinate of the footprint.</summary>
    public decimal MinX => CenterXMillimeters - (SizeXMillimeters / 2m);

    /// <summary>Gets the maximum X coordinate of the footprint.</summary>
    public decimal MaxX => CenterXMillimeters + (SizeXMillimeters / 2m);

    /// <summary>Gets the minimum Y coordinate of the footprint.</summary>
    public decimal MinY => CenterYMillimeters - (SizeYMillimeters / 2m);

    /// <summary>Gets the maximum Y coordinate of the footprint.</summary>
    public decimal MaxY => CenterYMillimeters + (SizeYMillimeters / 2m);
}

/// <summary>
/// The canonical, immutable calibration specification body. The digest is computed over this type.
/// </summary>
/// <remarks>
/// Field order in this record has no effect on the digest: the canonicalizer orders every object
/// member ordinally before hashing.
/// </remarks>
public sealed record CalibrationSpecificationDocument
{
    /// <summary>Gets the specification schema version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Gets the owning project identifier.</summary>
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

    /// <summary>Gets the sanitized printer configuration snapshot digest.</summary>
    public required string PrinterConfigurationSnapshotSha256 { get; init; }

    /// <summary>Gets when the snapshot was captured.</summary>
    public required DateTime SnapshotCapturedAtUtc { get; init; }

    /// <summary>Gets the calibration kind.</summary>
    public required string CalibrationKind { get; init; }

    /// <summary>Gets the canonical method name.</summary>
    public required string Method { get; init; }

    /// <summary>Gets the method definition version.</summary>
    public required string DefinitionVersion { get; init; }

    /// <summary>Gets the verified compatibility identity.</summary>
    public required CalibrationCompatibilityIdentity Compatibility { get; init; }

    /// <summary>Gets the verified firmware identity.</summary>
    public required CalibrationFirmwareContext Firmware { get; init; }

    /// <summary>Gets the toolhead and nozzle the calibration runs on.</summary>
    public required CalibrationToolheadContext Toolhead { get; init; }

    /// <summary>Gets the authoritative bed geometry.</summary>
    public required CalibrationBedGeometry Bed { get; init; }

    /// <summary>Gets the authoritative machine limits.</summary>
    public required CalibrationMachineLimits Limits { get; init; }

    /// <summary>Gets the filament product and optional spool snapshot.</summary>
    public required CalibrationFilamentContext Filament { get; init; }

    /// <summary>Gets the exact native profile triplet.</summary>
    public required CalibrationProfileTriplet Profiles { get; init; }

    /// <summary>Gets the generator identity.</summary>
    public required CalibrationGeneratorIdentity Generator { get; init; }

    /// <summary>Gets the resolved print parameters.</summary>
    public required CalibrationPrintParameters Print { get; init; }

    /// <summary>Gets the deterministic footprint the generator prints inside.</summary>
    public required CalibrationFootprint Footprint { get; init; }

    /// <summary>Gets the resolved sweep.</summary>
    public required CalibrationParameterSweep Sweep { get; init; }

    /// <summary>Gets the ordered deterministic segments.</summary>
    public required IReadOnlyList<CalibrationSegmentSpecification> Segments { get; init; }

    /// <summary>Gets the idempotency operation identifier.</summary>
    public required string OperationId { get; init; }

    /// <summary>Gets the linked imported asset, when the method requires one.</summary>
    public CalibrationModelReference? ImportedAsset { get; init; }
}

/// <summary>
/// A compiled calibration specification together with its canonical JSON and digest.
/// </summary>
/// <param name="Document">The canonical specification body.</param>
/// <param name="CanonicalJson">The canonical JSON text the digest was computed over.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 of <paramref name="CanonicalJson"/>.</param>
public sealed record CalibrationSpecification(
    CalibrationSpecificationDocument Document,
    string CanonicalJson,
    string Sha256);
