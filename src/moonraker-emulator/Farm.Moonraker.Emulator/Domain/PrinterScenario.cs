namespace Farm.Moonraker.Emulator.Domain;

/// <summary>
/// The four stable seed scenarios the emulator ships with, plus the states an
/// individual printer can be driven into at runtime via the control API.
/// </summary>
public enum PrinterScenario
{
    /// <summary>Klippy connected and idle, ready to accept a print.</summary>
    Ready,

    /// <summary>Klippy connected with an active print job in progress.</summary>
    Printing,

    /// <summary>Klippy connected with an active print job paused.</summary>
    Paused,

    /// <summary>Klippy has shut down (firmware restart / MCU fault); Klipper API server unreachable for printer objects.</summary>
    Shutdown,
}
