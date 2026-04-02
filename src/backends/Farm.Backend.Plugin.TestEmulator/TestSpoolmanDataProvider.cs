using Farm.Infrastructure;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Provides canned Spoolman data for test mode when TestEmulator:MockSpoolman is enabled.
/// Returns realistic spool and filament data without requiring a real Spoolman server.
/// </summary>
public static class TestSpoolmanDataProvider
{
    private static readonly SpoolmanVendorDto[] Vendors =
    [
        new(1, "Polymaker", null),
        new(2, "Prusament", null),
        new(3, "Hatchbox", null),
    ];

    private static readonly SpoolmanFilamentDto[] Filaments =
    [
        new(1, "PolyTerra PLA", "PLA", "FFFFFF", "Polymaker", 1.24, 1.75, 1000, 200, 24.99, 210, 60, "PM-70941", null, null, null),
        new(2, "Prusament PETG", "PETG", "000000", "Prusament", 1.27, 1.75, 1000, 230, 29.99, 240, 85, "PRU-PETG-BLK", null, null, null),
        new(3, "Hatchbox ASA", "ASA", "FF6B00", "Hatchbox", 1.07, 1.75, 1000, 200, 27.99, 250, 100, "HB-ASA-ORG", null, null, null),
    ];

    /// <summary>
    /// Returns 5 test spools with realistic metadata.
    /// </summary>
    public static List<SpoolmanSpoolDto> TestSpools =>
    [
        new(1, "PLA White #1", "PLA", 820, "FFFFFF", false,
            "PolyTerra PLA", "Polymaker", DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(-25), DateTime.UtcNow.AddDays(-1),
            1000, 180, 200, null, null, "Shelf A1", "LOT-2024-001", false, 24.99, null, 1),

        new(2, "PETG Black #1", "PETG", 650, "000000", true,
            "Prusament PETG", "Prusament", DateTime.UtcNow.AddDays(-20), DateTime.UtcNow.AddDays(-15), DateTime.UtcNow.AddHours(-2),
            1000, 350, 230, null, null, "Shelf A2", "LOT-2024-002", false, 29.99, null, 2),

        new(3, "ASA Orange #1", "ASA", 920, "FF6B00", false,
            "Hatchbox ASA", "Hatchbox", DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddDays(-3),
            1000, 80, 200, null, null, "Shelf B1", "LOT-2024-003", false, 27.99, null, 3),

        new(4, "TPU Flex Clear #1", "TPU", 450, "E0E0E0", false,
            "PolyTerra TPU", "Polymaker", DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-7),
            500, 50, 180, null, null, "Shelf B2", "LOT-2024-004", false, 34.99, null, 1),

        new(5, "ABS Red #1", "ABS", 780, "FF0000", false,
            "Prusament ABS", "Prusament", DateTime.UtcNow.AddDays(-15), DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-2),
            1000, 220, 230, null, null, "Shelf C1", "LOT-2024-005", false, 26.99, null, 2),
    ];

    /// <summary>
    /// Returns 3 test filament types matching the spools.
    /// </summary>
    public static List<SpoolmanFilamentDto> TestFilaments => [.. Filaments];

    /// <summary>
    /// Returns test vendors.
    /// </summary>
    public static List<SpoolmanVendorDto> TestVendors => [.. Vendors];

    /// <summary>
    /// Returns a healthy status response for the Spoolman health check.
    /// </summary>
    public static object HealthStatus => new
    {
        alive = true,
        version = "0.19.0-mock",
        debug_mode = false,
        automatic_backups = true,
    };
}
