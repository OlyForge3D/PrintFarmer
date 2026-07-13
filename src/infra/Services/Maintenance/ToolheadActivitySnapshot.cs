using System.Collections.ObjectModel;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Immutable, non-destructive view of a printer's pending active-tool telemetry.
/// </summary>
public sealed class ToolheadActivitySnapshot
{
    /// <summary>Creates an empty snapshot for a printer with no pending telemetry.</summary>
    public static ToolheadActivitySnapshot Empty(Guid printerId) => new(
        printerId,
        Guid.Empty,
        0,
        new Dictionary<int, double>(),
        new Dictionary<int, double>(),
        0,
        0,
        0,
        0,
        0);

    internal ToolheadActivitySnapshot(
        Guid printerId,
        Guid generation,
        long throughSequence,
        IReadOnlyDictionary<int, double> activeSeconds,
        IReadOnlyDictionary<int, double> cumulativeActiveSeconds,
        double recognizedSeconds,
        double windowSeconds,
        double cumulativeWindowSeconds,
        double knownIdleSeconds,
        double cumulativeKnownIdleSeconds)
    {
        PrinterId = printerId;
        Generation = generation;
        ThroughSequence = throughSequence;
        ActiveSeconds = new ReadOnlyDictionary<int, double>(new Dictionary<int, double>(activeSeconds));
        CumulativeActiveSeconds =
            new ReadOnlyDictionary<int, double>(new Dictionary<int, double>(cumulativeActiveSeconds));
        RecognizedSeconds = recognizedSeconds;
        WindowSeconds = windowSeconds;
        CumulativeWindowSeconds = cumulativeWindowSeconds;
        KnownIdleSeconds = knownIdleSeconds;
        CumulativeKnownIdleSeconds = cumulativeKnownIdleSeconds;
    }

    /// <summary>Gets the printer represented by this snapshot.</summary>
    public Guid PrinterId { get; }

    /// <summary>Gets known active-and-printing seconds by backend tool index.</summary>
    public IReadOnlyDictionary<int, double> ActiveSeconds { get; }

    /// <summary>Gets the sum of known active-and-printing seconds in this snapshot.</summary>
    public double RecognizedSeconds { get; }

    /// <summary>
    /// Gets the complete monotonic elapsed window represented by this snapshot, including idle,
    /// unknown-tool, and dropped-telemetry segments.
    /// </summary>
    public double WindowSeconds { get; }

    /// <summary>
    /// Gets the portion of <see cref="WindowSeconds"/> that was CONFIRMED not-printing based on
    /// fresh telemetry (issue #711, round-19 V19-1/H19-1) — recorded via
    /// <see cref="IToolheadActivityAccumulator.SampleKnownIdle"/>. A caller computing coverage should
    /// subtract this from <see cref="WindowSeconds"/> before dividing, because known idle time is a
    /// confirmed absence of print, not missing telemetry, and must never dilute the coverage ratio.
    /// </summary>
    public double KnownIdleSeconds { get; }

    internal Guid Generation { get; }

    internal long ThroughSequence { get; }

    internal IReadOnlyDictionary<int, double> CumulativeActiveSeconds { get; }

    internal double CumulativeWindowSeconds { get; }

    internal double CumulativeKnownIdleSeconds { get; }
}
