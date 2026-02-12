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
    bool SupportsFilamentControl = false)
{
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

            return caps.ToArray();
        }
    }
}
