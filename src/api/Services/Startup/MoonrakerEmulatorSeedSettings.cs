namespace Farm.Web.Api.Services.Startup;

/// <summary>
/// Configures deterministic Moonraker-backed printers for isolated validation environments.
/// </summary>
public sealed class MoonrakerEmulatorSeedSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MoonrakerEmulatorSeed";

    /// <summary>Whether emulator printer records should be seeded.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the authenticated development-only application state reset endpoint is enabled.
    /// </summary>
    public bool EnableControlApi { get; set; }

    /// <summary>Deterministic printer records to create when seeding is enabled.</summary>
    public List<MoonrakerEmulatorPrinterSeed> Printers { get; set; } =
    [
        new(
            Guid.Parse("6b68328f-6495-4d32-8a2d-784119e59a01"),
            "Moonraker Ready",
            BuildLocalUrl("moonraker-ready"),
            "Voron Design",
            "V2.4"),
        new(
            Guid.Parse("6b68328f-6495-4d32-8a2d-784119e59a02"),
            "Moonraker Printing",
            BuildLocalUrl("moonraker-printing"),
            "Voron Design",
            "V2.4")
        {
            ActiveJobId = Guid.Parse("7c79439f-75a6-4e43-9b3e-89522af6ab02"),
            ActiveJobStatus = PrintJobStatus.Printing,
        },
        new(
            Guid.Parse("6b68328f-6495-4d32-8a2d-784119e59a03"),
            "Moonraker Paused",
            BuildLocalUrl("moonraker-paused"),
            "Voron Design",
            "V2.4")
        {
            ActiveJobId = Guid.Parse("7c79439f-75a6-4e43-9b3e-89522af6ab03"),
            ActiveJobStatus = PrintJobStatus.Paused,
        },
        new(
            Guid.Parse("6b68328f-6495-4d32-8a2d-784119e59a04"),
            "Moonraker Shutdown",
            BuildLocalUrl("moonraker-shutdown"),
            "Voron Design",
            "V2.4"),
        new(
            Guid.Parse("6b68328f-6495-4d32-8a2d-784119e59a05"),
            "Moonraker Offline",
            BuildLocalUrl("moonraker-offline"),
            "Voron Design",
            "V2.4"),
    ];

    private static string BuildLocalUrl(string host) =>
            new UriBuilder(Uri.UriSchemeHttp, host, 7125).Uri.GetLeftPart(UriPartial.Authority);
}

/// <summary>
/// Describes one deterministic Moonraker printer record.
/// </summary>
public sealed record MoonrakerEmulatorPrinterSeed(
    Guid Id,
    string Name,
    string ServerUrl,
    string Manufacturer,
    string Model)
{
    /// <summary>Whether this printer should be enabled after seeding.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Stable active job identity for scenarios that begin with an occupied printer.</summary>
    public Guid? ActiveJobId { get; init; }

    /// <summary>Initial queue state matching the emulator's active print scenario.</summary>
    public PrintJobStatus? ActiveJobStatus { get; init; }
}
