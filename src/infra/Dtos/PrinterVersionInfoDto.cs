using System;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Infrastructure;

/// <summary>
/// Version information for a specific printer backend.
/// Values are best-effort and may be null/empty when not available.
/// </summary>
/// <param name="PrinterId">The printer this version information was retrieved for.</param>
/// <param name="Backend">The printer's backend implementation.</param>
/// <param name="Supported">Whether this backend implements <c>ISupportsPrinterInformation</c>.</param>
/// <param name="FirmwareVersion">The firmware version reading, when available.</param>
/// <param name="BackendVersion">The backend software version reading, when available.</param>
/// <param name="ApiVersion">The backend API version reading, when available.</param>
/// <param name="RetrievedAtUtc">When this version information was retrieved.</param>
/// <param name="Message">An optional human-readable message, e.g. describing a probe failure.</param>
/// <param name="RecordedFirmwareIdentity">
/// The recorded/persisted firmware identity from the printer's <c>Firmware*</c> columns — the
/// same authoritative fact <c>PrinterCalibrationContextService.ValidateFirmware</c> reads.
/// Non-null only for backends whose live firmware reading is read-through-persisted (currently
/// Moonraker/Klipper, #1656) AND only once
/// <see cref="CalibrationFirmwareIdentityDto.HasRecordedIdentity"/> is true for the printer
/// (#1656, PR #1660 review round 5/Hicks): a never-probed printer whose very first probe attempt
/// fails still has <c>FirmwareFamily == Unknown</c>/<c>FirmwareVersion == null</c>, and must
/// report <c>null</c> here — not a semantically-empty
/// <see cref="CalibrationFirmwareIdentityDto"/> — so the UI can never render "Recorded" for a
/// printer the calibration gate still considers to have no firmware identity at all. Always null
/// for backends that only ever report a thin, non-persisted live probe (e.g. PrusaLink,
/// OctoPrint, SDCP), since those can never satisfy the Klipper-only calibration gate regardless
/// of the value shown here.
/// </param>
public sealed record PrinterVersionInfoDto(
    Guid PrinterId,
    PrinterBackend Backend,
    bool Supported,
    string? FirmwareVersion,
    string? BackendVersion,
    string? ApiVersion,
    DateTime RetrievedAtUtc,
    string? Message = null,
    CalibrationFirmwareIdentityDto? RecordedFirmwareIdentity = null);
