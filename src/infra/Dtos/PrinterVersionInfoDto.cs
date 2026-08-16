using System;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

namespace Farm.Infrastructure;

/// <summary>
/// Version information for a specific printer backend.
/// Values are best-effort and may be null/empty when not available.
/// </summary>
/// <param name="RecordedFirmwareIdentity">
/// The recorded/persisted firmware identity from the printer's <c>Firmware*</c> columns — the
/// same authoritative fact <c>PrinterCalibrationContextService.ValidateFirmware</c> reads.
/// Non-null only for backends whose live firmware reading is read-through-persisted (currently
/// Moonraker/Klipper, #1656); null for backends that only ever report a thin, non-persisted live
/// probe (e.g. PrusaLink, OctoPrint, SDCP), since those can never satisfy the Klipper-only
/// calibration gate regardless of the value shown here.
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

