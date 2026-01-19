namespace Farm.Infrastructure;

/// <summary>
/// Request DTO for cloning profiles from a template machine profile to a custom printer.
/// </summary>
public class CloneProfilesRequestDto
{
    public Guid SourceMachineProfileId { get; set; } // Machine profile to clone from (e.g., "Prusa CORE One")

    public Guid TargetPrinterId { get; set; } // Printer to clone profiles to (e.g., "Prusa CORE One L custom instance)
}

/// <summary>
/// Response DTO for profile cloning operation results.
/// </summary>
public class CloneProfilesResponseDto
{
    public Guid SourceMachineProfileId { get; set; }

    public string SourceMachineName { get; set; } = string.Empty;

    public Guid TargetPrinterId { get; set; }

    public string TargetPrinterName { get; set; } = string.Empty;

    public int ProcessProfilesCloned { get; set; }

    public int FilamentProfilesCloned { get; set; }

    public int TotalProfilesCloned { get; set; }
}
