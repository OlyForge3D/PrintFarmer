using Farm.Slicer.Module.Domain;

namespace Farm.Slicer.Module.Models;

/// <summary>
/// Canonical, validated string names for <see cref="SlicerEngineType"/>.
/// </summary>
/// <remarks>
/// The public contract serializes the engine as a validated string. Unknown values are rejected by
/// the caller with <c>400</c>; they are never cast to an undefined enum member and never silently
/// fall back to a default engine.
/// </remarks>
public static class SlicerEngineNames
{
    /// <summary>The single engine supported by the calibration production path.</summary>
    public const string OrcaSlicer = "OrcaSlicer";

    /// <summary>
    /// Determines whether the supplied value is a defined <see cref="SlicerEngineType"/> member.
    /// </summary>
    /// <param name="engine">The candidate engine value produced by model binding.</param>
    /// <returns><see langword="true"/> when the value maps to a declared engine.</returns>
    /// <example>
    /// <code>
    /// if (!SlicerEngineNames.IsDefined(request.SlicerEngine))
    /// {
    ///     return BadRequest();
    /// }
    /// </code>
    /// </example>
    public static bool IsDefined(SlicerEngineType engine) => Enum.IsDefined(engine);

    /// <summary>
    /// Parses a canonical engine name. Numeric text is rejected so callers cannot bypass validation.
    /// </summary>
    /// <param name="value">Canonical engine name such as <c>OrcaSlicer</c>.</param>
    /// <param name="engine">The parsed engine when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a declared engine name.</returns>
    public static bool TryParse(string? value, out SlicerEngineType engine)
    {
        engine = default;
        if (string.IsNullOrWhiteSpace(value) || char.IsAsciiDigit(value.Trim()[0]))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out engine) && IsDefined(engine);
    }

    /// <summary>
    /// Resolves the authoritative engine for a persisted job.
    /// </summary>
    /// <param name="job">The persisted slice job.</param>
    /// <returns>
    /// The canonical engine recorded on the job.
    /// </returns>
    /// <remarks>
    /// Rows created before the canonical contract only carry a numeric discriminator that was
    /// written from two different enumerations with incompatible ordering, so it cannot be decoded
    /// reliably. Those rows resolve to <see cref="SlicerEngineType.OrcaSlicer"/>, which is the only
    /// engine the worker fleet runs, so pre-existing jobs stay claimable. This compatibility
    /// mapping never applies to a request value: an unknown engine on the wire is rejected.
    /// </remarks>
    public static SlicerEngineType Resolve(SliceJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return TryParse(job.SlicerEngineName, out SlicerEngineType named)
            ? named
            : SlicerEngineType.OrcaSlicer;
    }

    /// <summary>
    /// Maps an engine to the worker capability tag that a worker must advertise to run it.
    /// </summary>
    /// <param name="engine">The engine required by the job.</param>
    /// <returns>The lowercase capability tag advertised by workers.</returns>
    public static string ToCapabilityTag(SlicerEngineType engine) => engine switch
    {
        SlicerEngineType.OrcaSlicer => "orcaslicer",
        SlicerEngineType.PrusaSlicer => "prusaslicer",
        SlicerEngineType.SuperSlicer => "superslicer",
        SlicerEngineType.Cura => "cura",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown slicer engine."),
    };
}
