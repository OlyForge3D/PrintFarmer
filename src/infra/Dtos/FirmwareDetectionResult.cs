using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Why an on-demand firmware detection probe did not persist a firmware identity. Distinguishing
/// these matters because the remedies are entirely different: a wrong backend is a configuration
/// error, whereas an unreachable endpoint is an operational one.
/// </summary>
public enum FirmwareDetectionFailure
{
    /// <summary>The probe succeeded; no failure.</summary>
    None = 0,

    /// <summary>No printer exists with the requested id.</summary>
    PrinterNotFound,

    /// <summary>The printer is not a Moonraker backend, which is the only probe implemented.</summary>
    BackendNotSupported,

    /// <summary>The printer's stored server URL is not a usable absolute URI.</summary>
    ServerUrlInvalid,

    /// <summary>Every candidate Moonraker endpoint failed to answer.</summary>
    ProbeFailed,
}

/// <summary>
/// Outcome of an on-demand firmware detection probe against a registered printer.
/// </summary>
/// <param name="Succeeded">True when a firmware identity was probed and persisted.</param>
/// <param name="Failure">Why the probe did not persist an identity; <see cref="FirmwareDetectionFailure.None"/> on success.</param>
/// <param name="Family">The detected firmware family, when the probe succeeded.</param>
/// <param name="Version">The detected firmware version, when the probe reported one.</param>
/// <param name="DetectionConfidence">Normalized 0.0-1.0 detection confidence, when the probe succeeded.</param>
/// <param name="DetectedAtUtc">When the probe ran, when it succeeded.</param>
/// <param name="IdentityVerified">
/// Whether a human has attested this identity. A probe never sets this — it is echoed so a caller
/// can tell that detection alone still leaves calibration firmware verification unsatisfied.
/// </param>
public sealed record FirmwareDetectionResult(
    bool Succeeded,
    FirmwareDetectionFailure Failure,
    PrinterFirmwareFamily? Family = null,
    string? Version = null,
    decimal? DetectionConfidence = null,
    DateTime? DetectedAtUtc = null,
    bool IdentityVerified = false)
{
    public static FirmwareDetectionResult Failed(FirmwareDetectionFailure failure) =>
        new(Succeeded: false, Failure: failure);
}
