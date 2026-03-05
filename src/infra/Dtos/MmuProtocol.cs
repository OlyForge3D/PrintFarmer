namespace Farm.Infrastructure;

/// <summary>
/// Well-known MMU protocol type identifiers used throughout the backend and sent to the frontend.
/// </summary>
public static class MmuProtocol
{
    /// <summary>Happy Hare MMU/ERCF protocol via Moonraker.</summary>
    public const string HappyHare = "HappyHare";

    /// <summary>Qidibox filament box protocol via Moonraker.</summary>
    public const string Qidibox = "Qidibox";

    /// <summary>AFC (BoxTurtle/NightOwl/QuattroBox) filament changer protocol via Moonraker.</summary>
    public const string Afc = "AFC";

    /// <summary>Default/fallback when protocol has not yet been identified.</summary>
    public const string Unknown = "Unknown";
}
