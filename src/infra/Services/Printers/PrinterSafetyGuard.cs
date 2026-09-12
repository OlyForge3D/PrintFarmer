namespace Farm.Infrastructure.Services.Printers;

/// <summary>Safety-sensitive operations governed by verified printer evidence.</summary>
public enum PrinterSafetyOperation
{
    /// <summary>Absolute movement in the backend coordinate frame.</summary>
    AbsoluteMovement,

    /// <summary>Persistent firmware Z-offset save.</summary>
    FirmwareZOffsetSave,

    /// <summary>Physical filament load.</summary>
    FilamentLoad,

    /// <summary>Physical filament unload.</summary>
    FilamentUnload,

    /// <summary>Physical filament change.</summary>
    FilamentChange,

    /// <summary>Happy Hare MMU tool change.</summary>
    MmuChangeTool,

    /// <summary>Happy Hare MMU filament load.</summary>
    MmuLoad,

    /// <summary>Happy Hare MMU filament eject.</summary>
    MmuEject,

    /// <summary>Direct extrusion or retraction.</summary>
    Extrusion,
}

/// <summary>Absolute movement coordinates to validate before dispatch.</summary>
public sealed record PrinterSafetyMoveRequest(
    double? X,
    double? Y,
    double? Z);

/// <summary>Fail-closed result of server-side safety revalidation.</summary>
public sealed record PrinterSafetyValidationResult(
    bool Success,
    int StatusCode = 200,
    string? Code = null,
    string? Detail = null)
{
    /// <summary>Successful validation result.</summary>
    public static PrinterSafetyValidationResult Allowed { get; } = new(true);

    /// <summary>Creates a rejected validation result.</summary>
    public static PrinterSafetyValidationResult Reject(
        int statusCode,
        string code,
        string detail) =>
        new(false, statusCode, code, detail);
}

/// <summary>Revalidates verified capabilities and live telemetry before physical dispatch.</summary>
public interface IPrinterSafetyGuard
{
    /// <summary>Validates one safety-sensitive operation using the server clock.</summary>
    /// <param name="printerId">Printer identifier.</param>
    /// <param name="operation">Operation about to be dispatched.</param>
    /// <param name="move">Absolute movement coordinates, when applicable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A typed fail-closed validation result.</returns>
    Task<PrinterSafetyValidationResult> ValidateAsync(
        Guid printerId,
        PrinterSafetyOperation operation,
        PrinterSafetyMoveRequest? move,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class PrinterSafetyGuard(
    IPrinterBackendCapabilitiesService capabilitiesService,
    IPrinterStatusCacheReader statusCache,
    TimeProvider timeProvider) : IPrinterSafetyGuard
{
    private readonly IPrinterBackendCapabilitiesService _capabilitiesService =
        capabilitiesService ??
        throw new ArgumentNullException(nameof(capabilitiesService));

    private readonly IPrinterStatusCacheReader _statusCache =
        statusCache ?? throw new ArgumentNullException(nameof(statusCache));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public async Task<PrinterSafetyValidationResult> ValidateAsync(
        Guid printerId,
        PrinterSafetyOperation operation,
        PrinterSafetyMoveRequest? move,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _capabilitiesService.InvalidateVerifiedSafety(printerId);
        PrinterBackendCapabilitiesDto? capabilities =
            await _capabilitiesService.GetByPrinterIdAsync(printerId, ct);
        if (capabilities is null)
        {
            return PrinterSafetyValidationResult.Reject(
                503,
                "printer_safety_evidence_unknown",
                "Verified printer safety evidence is unavailable.");
        }

        PrinterVerifiedSafetyDto safety = capabilities.VerifiedSafety;
        if (operation != PrinterSafetyOperation.Extrusion)
        {
            PrinterSafetyValidationResult support =
                ValidateSupport(GetOperationCapability(safety, operation));
            if (!support.Success)
            {
                return support;
            }
        }
        else if (!capabilities.SupportsExtrusion)
        {
            return PrinterSafetyValidationResult.Reject(
                422,
                "printer_operation_unsupported",
                "The printer does not support bounded extrusion.");
        }

        return operation switch
        {
            PrinterSafetyOperation.AbsoluteMovement =>
                ValidateAbsoluteMovement(
                    safety,
                    _statusCache.GetStatus(printerId),
                    move,
                    _timeProvider.GetUtcNow().UtcDateTime),
            PrinterSafetyOperation.FilamentLoad or
            PrinterSafetyOperation.FilamentUnload or
            PrinterSafetyOperation.FilamentChange or
            PrinterSafetyOperation.MmuChangeTool or
            PrinterSafetyOperation.MmuLoad or
            PrinterSafetyOperation.MmuEject or
            PrinterSafetyOperation.Extrusion =>
                ValidateExtrusion(
                    safety,
                    _statusCache.GetStatus(printerId),
                    _timeProvider.GetUtcNow().UtcDateTime),
            _ => PrinterSafetyValidationResult.Allowed,
        };
    }

    private static VerifiedSafetyOperationCapabilityDto GetOperationCapability(
        PrinterVerifiedSafetyDto safety,
        PrinterSafetyOperation operation) =>
        operation switch
        {
            PrinterSafetyOperation.AbsoluteMovement =>
                safety.Operations.AbsoluteMovement,
            PrinterSafetyOperation.FirmwareZOffsetSave =>
                safety.Operations.FirmwareZOffsetSave,
            PrinterSafetyOperation.FilamentLoad =>
                safety.Operations.FilamentLoad,
            PrinterSafetyOperation.FilamentUnload =>
                safety.Operations.FilamentUnload,
            PrinterSafetyOperation.FilamentChange =>
                safety.Operations.FilamentChange,
            PrinterSafetyOperation.MmuChangeTool =>
                safety.Operations.MmuChangeTool,
            PrinterSafetyOperation.MmuLoad =>
                safety.Operations.MmuLoad,
            PrinterSafetyOperation.MmuEject =>
                safety.Operations.MmuEject,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static PrinterSafetyValidationResult ValidateSupport(
        VerifiedSafetyOperationCapabilityDto capability) =>
        capability.Support switch
        {
            VerifiedSafetySupport.Supported =>
                PrinterSafetyValidationResult.Allowed,
            VerifiedSafetySupport.Unsupported =>
                PrinterSafetyValidationResult.Reject(
                    422,
                    "printer_operation_unsupported",
                    "An authoritative backend probe proved that the operation is unsupported."),
            _ => PrinterSafetyValidationResult.Reject(
                503,
                "printer_safety_evidence_unknown",
                "The backend has not authoritatively established operation support."),
        };

    private static PrinterSafetyValidationResult ValidateExtrusion(
        PrinterVerifiedSafetyDto safety,
        PrinterStatusDto? status,
        DateTime utcNow)
    {
        VerifiedSafetyScalarFactDto minimum =
            safety.Extrusion.MinimumSafeMeasuredHotendTemperatureC;
        if (minimum.State != VerifiedSafetyFactState.Verified ||
            minimum.Value is not double minimumValue ||
            !double.IsFinite(minimumValue))
        {
            return PrinterSafetyValidationResult.Reject(
                503,
                "printer_safety_evidence_unknown",
                "A verified material-safe measured hotend temperature is unavailable.");
        }

        if (status?.IsOnline != true)
        {
            return MissingTelemetry("The printer is offline or has no live status.");
        }

        SafetyScalarTelemetryFactDto measured =
            (status.SafetyTelemetry ?? PrinterSafetyTelemetryDto.Empty)
            .MeasuredHotendTemperatureC;
        PrinterSafetyValidationResult freshness =
            ValidateFreshFact(
                measured.Value,
                measured.ObservedAtUtc,
                measured.StaleAfterSeconds,
                utcNow,
                "measured hotend temperature");
        if (!freshness.Success)
        {
            return freshness;
        }

        return measured.Value < minimumValue
            ? PrinterSafetyValidationResult.Reject(
                409,
                "printer_temperature_below_minimum",
                "The fresh measured hotend temperature is below the verified safe minimum.")
            : PrinterSafetyValidationResult.Allowed;
    }

    private static PrinterSafetyValidationResult ValidateAbsoluteMovement(
        PrinterVerifiedSafetyDto safety,
        PrinterStatusDto? status,
        PrinterSafetyMoveRequest? move,
        DateTime utcNow)
    {
        if (safety.Positioning.CoordinateOriginMm is not
                { State: VerifiedSafetyFactState.Verified, Value: { } origin } ||
            !IsFinite(origin) ||
            safety.Positioning.TravelEnvelopeMm is not
                { State: VerifiedSafetyFactState.Verified, Value: { } envelope } ||
            !IsValidEnvelope(envelope) ||
            safety.Positioning.MinimumClearanceZMm is not
                { State: VerifiedSafetyFactState.Verified, Value: double clearance } ||
            !double.IsFinite(clearance))
        {
            return PrinterSafetyValidationResult.Reject(
                503,
                "printer_safety_evidence_unknown",
                "Verified coordinate origin, travel bounds, and clearance are required.");
        }

        if (status?.IsOnline != true)
        {
            return MissingTelemetry("The printer is offline or has no live status.");
        }

        PrinterSafetyTelemetryDto telemetry =
            status.SafetyTelemetry ?? PrinterSafetyTelemetryDto.Empty;
        PrinterSafetyValidationResult homedFreshness = ValidateFreshFact(
            telemetry.HomedAxes.Value,
            telemetry.HomedAxes.ObservedAtUtc,
            telemetry.HomedAxes.StaleAfterSeconds,
            utcNow,
            "homed axes");
        if (!homedFreshness.Success)
        {
            return homedFreshness;
        }

        HashSet<string> homedAxes = new(
            telemetry.HomedAxes.Value ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (!homedAxes.IsSupersetOf(["x", "y", "z"]))
        {
            return PrinterSafetyValidationResult.Reject(
                409,
                "printer_axes_not_homed",
                "All movement axes must be freshly reported as homed.");
        }

        PrinterSafetyValidationResult offsetFreshness = ValidateFreshFact(
            telemetry.CoordinateOriginOffsetMm.Value,
            telemetry.CoordinateOriginOffsetMm.ObservedAtUtc,
            telemetry.CoordinateOriginOffsetMm.StaleAfterSeconds,
            utcNow,
            "coordinate origin offset");
        if (!offsetFreshness.Success)
        {
            return offsetFreshness;
        }

        if (move is not { X: double x, Y: double y, Z: double z } ||
            !double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(z) ||
            telemetry.CoordinateOriginOffsetMm.Value is not { } offset ||
            !IsFinite(offset))
        {
            return PrinterSafetyValidationResult.Reject(
                409,
                "printer_move_out_of_bounds",
                "Finite X, Y, and Z coordinates in the verified movement frame are required.");
        }

        var effective = new SafetyVector3Dto(
            x + offset.X,
            y + offset.Y,
            z + offset.Z);
        if (!Contains(envelope, effective))
        {
            return PrinterSafetyValidationResult.Reject(
                409,
                "printer_move_out_of_bounds",
                "The requested move is outside the verified travel envelope.");
        }

        return effective.Z < clearance
            ? PrinterSafetyValidationResult.Reject(
                409,
                "printer_clearance_not_met",
                "The requested move does not meet the verified minimum Z clearance.")
            : PrinterSafetyValidationResult.Allowed;
    }

    private static PrinterSafetyValidationResult ValidateFreshFact<T>(
        T? value,
        DateTime? observedAtUtc,
        int staleAfterSeconds,
        DateTime utcNow,
        string factName)
    {
        if (value is null || observedAtUtc is null)
        {
            return MissingTelemetry($"Fresh {factName} telemetry is missing.");
        }

        if (staleAfterSeconds <= 0 ||
            observedAtUtc.Value > utcNow ||
            utcNow - observedAtUtc.Value >
            TimeSpan.FromSeconds(staleAfterSeconds))
        {
            return PrinterSafetyValidationResult.Reject(
                503,
                "printer_telemetry_stale",
                $"The {factName} telemetry is stale or future-dated.");
        }

        if (value is double scalar && !double.IsFinite(scalar))
        {
            return MissingTelemetry($"The {factName} telemetry is not finite.");
        }

        return PrinterSafetyValidationResult.Allowed;
    }

    private static PrinterSafetyValidationResult MissingTelemetry(string detail) =>
        PrinterSafetyValidationResult.Reject(
            503,
            "printer_telemetry_missing",
            detail);

    private static bool IsFinite(SafetyVector3Dto value) =>
        double.IsFinite(value.X) &&
        double.IsFinite(value.Y) &&
        double.IsFinite(value.Z);

    private static bool IsValidEnvelope(SafetyTravelEnvelopeDto envelope) =>
        IsFinite(envelope.Minimum) &&
        IsFinite(envelope.Maximum) &&
        envelope.Minimum.X <= envelope.Maximum.X &&
        envelope.Minimum.Y <= envelope.Maximum.Y &&
        envelope.Minimum.Z <= envelope.Maximum.Z;

    private static bool Contains(
        SafetyTravelEnvelopeDto envelope,
        SafetyVector3Dto value) =>
        value.X >= envelope.Minimum.X &&
        value.X <= envelope.Maximum.X &&
        value.Y >= envelope.Minimum.Y &&
        value.Y <= envelope.Maximum.Y &&
        value.Z >= envelope.Minimum.Z &&
        value.Z <= envelope.Maximum.Z;
}
