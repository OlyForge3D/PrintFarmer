#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;

public class PrinterInformation
{
    public string Name { get; set; } = string.Empty;

    public string? Location { get; set; }

    public string Serial { get; set; } = string.Empty;

    public string? Hostname { get; set; }

    public string FirmwareVersion { get; set; } = string.Empty;

    public string PrusaLinkVersion { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = string.Empty;

    public double NozzleDiameter { get; set; }

    public int MinExtrusionTemp { get; set; }

    public bool HasMmu { get; set; }

    public bool SdCardReady { get; set; }

    public bool HasActiveCamera { get; set; }

    public bool SupportsUploadByPut { get; set; }
}

#pragma warning restore CS1066
