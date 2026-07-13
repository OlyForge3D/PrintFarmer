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
        0);

    internal ToolheadActivitySnapshot(
        Guid printerId,
        Guid generation,
        long throughSequence,
        IReadOnlyDictionary<int, double> activeSeconds,
        IReadOnlyDictionary<int, double> cumulativeActiveSeconds,
        double recognizedSeconds,
        double windowSeconds,
        double cumulativeWindowSeconds)
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

    internal Guid Generation { get; }

    internal long ThroughSequence { get; }

    internal IReadOnlyDictionary<int, double> CumulativeActiveSeconds { get; }

    internal double CumulativeWindowSeconds { get; }
}
