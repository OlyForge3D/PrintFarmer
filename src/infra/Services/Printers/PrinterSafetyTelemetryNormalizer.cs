namespace Farm.Infrastructure.Services.Printers;

/// <summary>Creates complete fact-specific safety telemetry at backend receipt time.</summary>
public static class PrinterSafetyTelemetryNormalizer
{
    /// <summary>Attaches or preserves fact-specific telemetry observations.</summary>
    /// <param name="status">Latest backend status.</param>
    /// <param name="existing">Previous status, used only for omitted fields.</param>
    /// <param name="observedAtUtc">Time this backend status reached the server.</param>
    /// <returns>Status with a complete <see cref="PrinterSafetyTelemetryDto"/> object.</returns>
    public static PrinterStatusDto Normalize(
        PrinterStatusDto status,
        PrinterStatusDto? existing,
        DateTime observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        PrinterSafetyTelemetryDto previous =
            existing?.SafetyTelemetry ?? PrinterSafetyTelemetryDto.Empty;
        PrinterSafetyTelemetryDto? supplied = status.SafetyTelemetry;

        SafetyScalarTelemetryFactDto measured =
            supplied?.MeasuredHotendTemperatureC.ObservedAtUtc is not null
                ? supplied.MeasuredHotendTemperatureC
                : status.HotendTemp.HasValue
                    ? new SafetyScalarTelemetryFactDto(
                        status.HotendTemp,
                        observedAtUtc,
                        PrinterSafetyTelemetryDto.DefaultStaleAfterSeconds,
                        "backend.status.hotendTemp")
                    : previous.MeasuredHotendTemperatureC;
        SafetyScalarTelemetryFactDto target =
            supplied?.TargetHotendTemperatureC.ObservedAtUtc is not null
                ? supplied.TargetHotendTemperatureC
                : status.HotendTarget.HasValue
                    ? new SafetyScalarTelemetryFactDto(
                        status.HotendTarget,
                        observedAtUtc,
                        PrinterSafetyTelemetryDto.DefaultStaleAfterSeconds,
                        "backend.status.hotendTarget")
                    : previous.TargetHotendTemperatureC;
        SafetyAxesTelemetryFactDto homedAxes =
            supplied?.HomedAxes.ObservedAtUtc is not null
                ? supplied.HomedAxes
                : status.HomedAxes is not null
                    ? new SafetyAxesTelemetryFactDto(
                        status.HomedAxes
                            .Where(char.IsAsciiLetter)
                            .Select(axis => char.ToLowerInvariant(axis).ToString())
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        observedAtUtc,
                        PrinterSafetyTelemetryDto.DefaultStaleAfterSeconds,
                        "backend.status.homedAxes")
                    : previous.HomedAxes;
        SafetyVectorTelemetryFactDto originOffset =
            supplied?.CoordinateOriginOffsetMm.ObservedAtUtc is not null
                ? supplied.CoordinateOriginOffsetMm
                : previous.CoordinateOriginOffsetMm;

        return status with
        {
            SafetyTelemetry = new PrinterSafetyTelemetryDto(
                measured,
                target,
                homedAxes,
                originOffset),
        };
    }
}
