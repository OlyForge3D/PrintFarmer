namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Configuration settings for the TestEmulator backend plugin.
/// </summary>
public class TestEmulatorSettings
{
    /// <summary>Section name in appsettings.json.</summary>
    public const string SectionName = "TestEmulator";

    /// <summary>Whether the test emulator is enabled. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Pre-configured emulated printers to seed on startup.</summary>
    public List<EmulatedPrinterConfig> Printers { get; set; } = [];

    /// <summary>Whether to override Spoolman endpoints with mock data.</summary>
    public bool MockSpoolman { get; set; }

    /// <summary>Whether to override discovery with mock results.</summary>
    public bool MockDiscovery { get; set; }
}

/// <summary>
/// Configuration for a single emulated printer.
/// </summary>
public class EmulatedPrinterConfig
{
    /// <summary>Display name for the emulated printer.</summary>
    public string Name { get; set; } = "Test Printer";

    /// <summary>Initial state: Idle, Printing, Error, Offline.</summary>
    public string InitialState { get; set; } = "Idle";

    /// <summary>Initial progress percentage when state is Printing (0-100).</summary>
    public double Progress { get; set; }

    /// <summary>Duration in seconds for a full print job (0-100% progress). Default 60s.</summary>
    public int PrintDurationSeconds { get; set; } = 60;
}
