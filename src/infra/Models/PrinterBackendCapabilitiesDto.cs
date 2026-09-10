using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Backend capabilities supported by a printer's backend implementation (Moonraker, PrusaLink, etc.).
/// Represents what operations the backend client supports via its plugin interfaces.
/// This is distinct from hardware capabilities (nozzle size, build volume, etc.).
/// </summary>
public record PrinterBackendCapabilitiesDto(
    Guid PrinterId,
    string PrinterName,
    PrinterBackend Backend,
    bool SupportsCamera = false,
    bool SupportsFileDownload = false,
    bool SupportsFileList = false,
    bool SupportsFileUpload = false,
    bool SupportsStartPrint = false,
    bool SupportsControlOperations = false,
    bool SupportsFileMetadata = false,
    bool SupportsMovement = false,
    bool SupportsTemperatureControl = false,
    bool SupportsPrinterInformation = false,
    bool SupportsHistory = false,
    bool SupportsFilamentControl = false,
    bool SupportsObjectExclusion = false)
{
    /// <summary>Whether the shared relative-move route has a verified implementation.</summary>
    public bool SupportsRelativeMovement { get; init; }

    /// <summary>Whether the shared absolute-move route has a verified implementation.</summary>
    public bool SupportsAbsoluteMovement { get; init; }

    /// <summary>Whether the backend can execute the shared motor-release command.</summary>
    public bool SupportsDisableMotors { get; init; }

    /// <summary>Whether the backend can execute bounded signed extrusion. Not proof of a hot nozzle.</summary>
    public bool SupportsExtrusion { get; init; }

    /// <summary>Whether a reviewed printer revision can save a database-only Z-offset.</summary>
    public bool SupportsZOffset { get; init; }

    /// <summary>Whether saving the offset persistently to firmware is proven, not just raw command transport.</summary>
    public bool SupportsZOffsetFirmwareSave { get; init; }

    /// <summary>Whether the shared home-all route is supported. Not the current homed state.</summary>
    public bool SupportsHoming { get; init; }

    /// <summary>Whether the shared XY-only homing route is supported.</summary>
    public bool SupportsHomingXY { get; init; }

    /// <summary>Whether the shared Z-only homing route is supported.</summary>
    public bool SupportsHomingZ { get; init; }

    /// <summary>Whether the shared temperature route supports the hotend target.</summary>
    public bool SupportsHotendTemperature { get; init; }

    /// <summary>Whether the shared temperature route supports the bed target.</summary>
    public bool SupportsBedTemperature { get; init; }

    /// <summary>Whether physical filament loading is proven for this printer.</summary>
    public bool SupportsFilamentLoad { get; init; }

    /// <summary>Whether physical filament unloading is proven for this printer.</summary>
    public bool SupportsFilamentUnload { get; init; }

    /// <summary>Whether a physical filament-change procedure is proven for this printer.</summary>
    public bool SupportsFilamentChange { get; init; }

    /// <summary>Lowercase axes accepted by proven movement or homing routes, not travel bounds or homed state.</summary>
    public string[] SupportedAxes { get; init; } = [];

    /// <summary>Authoritative per-printer facts for safety-sensitive operations.</summary>
    public PrinterVerifiedSafetyDto VerifiedSafety { get; init; } =
        PrinterVerifiedSafetyDto.Unknown();

    /// <summary>
    /// Gets a summary of all supported capabilities as a formatted string.
    /// </summary>
    [JsonIgnore]
    public string[] SupportedCapabilityNames
    {
        get
        {
            var caps = new List<string>();
            if (SupportsCamera)
            {
                caps.Add("Camera");
            }

            if (SupportsFileDownload)
            {
                caps.Add("FileDownload");
            }

            if (SupportsFileList)
            {
                caps.Add("FileList");
            }

            if (SupportsFileUpload)
            {
                caps.Add("FileUpload");
            }

            if (SupportsStartPrint)
            {
                caps.Add("StartPrint");
            }

            if (SupportsControlOperations)
            {
                caps.Add("ControlOperations");
            }

            if (SupportsFileMetadata)
            {
                caps.Add("FileMetadata");
            }

            if (SupportsMovement)
            {
                caps.Add("Movement");
            }

            if (SupportsTemperatureControl)
            {
                caps.Add("TemperatureControl");
            }

            if (SupportsPrinterInformation)
            {
                caps.Add("PrinterInformation");
            }

            if (SupportsHistory)
            {
                caps.Add("History");
            }

            if (SupportsFilamentControl)
            {
                caps.Add("FilamentControl");
            }

            if (SupportsObjectExclusion)
            {
                caps.Add("ObjectExclusion");
            }

            return caps.ToArray();
        }
    }
}
