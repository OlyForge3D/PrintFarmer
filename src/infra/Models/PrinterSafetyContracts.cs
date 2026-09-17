namespace Farm.Infrastructure;

/// <summary>Completeness of the latest per-printer safety discovery.</summary>
public enum VerifiedSafetyDiscoveryState
{
    /// <summary>Every fact required by the contract was authoritatively discovered.</summary>
    Verified,

    /// <summary>At least one fact was discovered, while other facts remain unknown.</summary>
    Partial,

    /// <summary>No authoritative discovery result is currently available.</summary>
    Unavailable,
}

/// <summary>Tri-state support for a safety-sensitive printer operation.</summary>
public enum VerifiedSafetySupport
{
    /// <summary>An authoritative per-printer probe proved support.</summary>
    Supported,

    /// <summary>An authoritative per-printer probe proved absence.</summary>
    Unsupported,

    /// <summary>Support has not been authoritatively established.</summary>
    Unknown,
}

/// <summary>Verification state for a safety fact.</summary>
public enum VerifiedSafetyFactState
{
    /// <summary>The value came from an authoritative source.</summary>
    Verified,

    /// <summary>No authoritative value is available.</summary>
    Unknown,
}

/// <summary>Three-dimensional millimeter coordinates.</summary>
public sealed record SafetyVector3Dto(double X, double Y, double Z);

/// <summary>Minimum and maximum travel coordinates in the backend movement frame.</summary>
public sealed record SafetyTravelEnvelopeDto(
    SafetyVector3Dto Minimum,
    SafetyVector3Dto Maximum);

/// <summary>Provenance for the latest safety discovery.</summary>
public sealed record VerifiedSafetyDiscoveryDto(
    VerifiedSafetyDiscoveryState State,
    DateTime? ObservedAtUtc,
    string? SourceRevision);

/// <summary>Verified support and provenance for one operation.</summary>
public sealed record VerifiedSafetyOperationCapabilityDto(
    VerifiedSafetySupport Support,
    string? Source,
    DateTime? ObservedAtUtc);

/// <summary>Verified operation support facts.</summary>
public sealed record VerifiedSafetyOperationsDto(
    VerifiedSafetyOperationCapabilityDto AbsoluteMovement,
    VerifiedSafetyOperationCapabilityDto FirmwareZOffsetSave,
    VerifiedSafetyOperationCapabilityDto FilamentLoad,
    VerifiedSafetyOperationCapabilityDto FilamentUnload,
    VerifiedSafetyOperationCapabilityDto FilamentChange,
    VerifiedSafetyOperationCapabilityDto MmuChangeTool,
    VerifiedSafetyOperationCapabilityDto MmuLoad,
    VerifiedSafetyOperationCapabilityDto MmuEject);

/// <summary>A verified scalar safety fact.</summary>
public sealed record VerifiedSafetyScalarFactDto(
    VerifiedSafetyFactState State,
    double? Value,
    string? Source,
    DateTime? ObservedAtUtc);

/// <summary>A verified vector safety fact.</summary>
public sealed record VerifiedSafetyVectorFactDto(
    VerifiedSafetyFactState State,
    SafetyVector3Dto? Value,
    string? Source,
    DateTime? ObservedAtUtc);

/// <summary>A verified travel-envelope safety fact.</summary>
public sealed record VerifiedSafetyEnvelopeFactDto(
    VerifiedSafetyFactState State,
    SafetyTravelEnvelopeDto? Value,
    string? Source,
    DateTime? ObservedAtUtc);

/// <summary>Verified extrusion safety facts.</summary>
public sealed record VerifiedSafetyExtrusionDto(
    VerifiedSafetyScalarFactDto MinimumSafeMeasuredHotendTemperatureC);

/// <summary>Verified positioning safety facts.</summary>
public sealed record VerifiedSafetyPositioningDto(
    VerifiedSafetyVectorFactDto CoordinateOriginMm,
    VerifiedSafetyEnvelopeFactDto TravelEnvelopeMm,
    VerifiedSafetyScalarFactDto MinimumClearanceZMm);

/// <summary>Versioned safety facts exposed by printer capability endpoints.</summary>
public sealed record PrinterVerifiedSafetyDto(
    int ContractVersion,
    VerifiedSafetyDiscoveryDto Discovery,
    VerifiedSafetyOperationsDto Operations,
    VerifiedSafetyExtrusionDto Extrusion,
    VerifiedSafetyPositioningDto Positioning)
{
    /// <summary>Creates a fail-closed contract with every fact unknown.</summary>
    public static PrinterVerifiedSafetyDto Unknown(
        string? source = null,
        DateTime? observedAtUtc = null,
        VerifiedSafetyDiscoveryState discoveryState = VerifiedSafetyDiscoveryState.Unavailable,
        string? sourceRevision = null)
    {
        var operation = new VerifiedSafetyOperationCapabilityDto(
            VerifiedSafetySupport.Unknown,
            source,
            observedAtUtc);
        var scalar = new VerifiedSafetyScalarFactDto(
            VerifiedSafetyFactState.Unknown,
            null,
            source,
            observedAtUtc);

        return new PrinterVerifiedSafetyDto(
            1,
            new VerifiedSafetyDiscoveryDto(discoveryState, observedAtUtc, sourceRevision),
            new VerifiedSafetyOperationsDto(
                operation,
                operation,
                operation,
                operation,
                operation,
                operation,
                operation,
                operation),
            new VerifiedSafetyExtrusionDto(scalar),
            new VerifiedSafetyPositioningDto(
                new VerifiedSafetyVectorFactDto(
                    VerifiedSafetyFactState.Unknown,
                    null,
                    source,
                    observedAtUtc),
                new VerifiedSafetyEnvelopeFactDto(
                    VerifiedSafetyFactState.Unknown,
                    null,
                    source,
                    observedAtUtc),
                scalar));
    }
}

/// <summary>A scalar telemetry observation with fact-specific freshness metadata.</summary>
public sealed record SafetyScalarTelemetryFactDto(
    double? Value,
    DateTime? ObservedAtUtc,
    int StaleAfterSeconds,
    string? Source);

/// <summary>An axes telemetry observation with fact-specific freshness metadata.</summary>
public sealed record SafetyAxesTelemetryFactDto(
    string[]? Value,
    DateTime? ObservedAtUtc,
    int StaleAfterSeconds,
    string? Source);

/// <summary>A vector telemetry observation with fact-specific freshness metadata.</summary>
public sealed record SafetyVectorTelemetryFactDto(
    SafetyVector3Dto? Value,
    DateTime? ObservedAtUtc,
    int StaleAfterSeconds,
    string? Source);

/// <summary>Live telemetry used only after applying each fact's freshness policy.</summary>
public sealed record PrinterSafetyTelemetryDto(
    SafetyScalarTelemetryFactDto MeasuredHotendTemperatureC,
    SafetyScalarTelemetryFactDto TargetHotendTemperatureC,
    SafetyAxesTelemetryFactDto HomedAxes,
    SafetyVectorTelemetryFactDto CoordinateOriginOffsetMm)
{
    /// <summary>Freshness window required by version 1 of the safety contract.</summary>
    public const int DefaultStaleAfterSeconds = 15;

    /// <summary>Creates a telemetry object with no observed facts.</summary>
    public static PrinterSafetyTelemetryDto Empty { get; } = new(
        new SafetyScalarTelemetryFactDto(null, null, DefaultStaleAfterSeconds, null),
        new SafetyScalarTelemetryFactDto(null, null, DefaultStaleAfterSeconds, null),
        new SafetyAxesTelemetryFactDto(null, null, DefaultStaleAfterSeconds, null),
        new SafetyVectorTelemetryFactDto(null, null, DefaultStaleAfterSeconds, null));
}
