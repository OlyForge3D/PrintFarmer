namespace Farm.Infrastructure.Domain;

/// <summary>Firmware families that may be asserted for calibration compatibility.</summary>
public enum PrinterFirmwareFamily
{
    Unknown = 0,
    Klipper = 1,
    Other = 99,
}

/// <summary>G-code dialects that may be asserted for calibration compatibility.</summary>
public enum PrinterGcodeDialect
{
    Unknown = 0,
    Klipper = 1,
    Other = 99,
}

/// <summary>Authoritative source used to identify printer firmware.</summary>
public enum FirmwareDetectionSource
{
    Unknown = 0,
    Printer = 1,
    Configured = 2,
}

/// <summary>Normalized motion systems used by calibration geometry.</summary>
public enum CalibrationMotionType
{
    Cartesian = 0,
    CoreXY = 1,
    Delta = 2,
    Unknown = 99,
}
