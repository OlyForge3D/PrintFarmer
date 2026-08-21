using System.Globalization;

namespace Farm.OrcaSlicer.Worker.Tests.Support;

/// <summary>
/// The axis-aligned XYZ extent a real OrcaSlicer G-code output occupied, read directly from its
/// motion commands rather than trusted slicer-reported metadata.
/// </summary>
/// <param name="SizeX">Extent along X, millimetres.</param>
/// <param name="SizeY">Extent along Y, millimetres.</param>
/// <param name="SizeZ">Extent along Z, millimetres.</param>
internal readonly record struct GcodeExtent(double SizeX, double SizeY, double SizeZ);

/// <summary>
/// Reads the axis-aligned bounding-box "size" (max minus min per axis) that a sliced object's
/// extruding moves occupy in a G-code file, so a real OrcaSlicer run's output can be compared
/// against an independently-computed expected orientation
/// (<see cref="OrientationMarkerGeometry.ComputeExpectedSize"/>).
/// </summary>
/// <remarks>
/// This assumes standard absolute XY positioning (<c>G90</c>) and absolute extrusion (the <c>E</c>
/// axis accumulates and is never reset mid-print by a relative-extrusion machine's layer-change
/// G-code) — <c>PinnedOrcaProfileCatalog</c> only ever selects a machine profile that declares
/// absolute extrusion, and OrcaSlicer's default is absolute XY positioning, so this holds for every
/// profile this suite can select. A stray <c>G91</c> is treated as an unsupported input rather than
/// silently mis-measured. <c>M82</c>/<c>M83</c> extrusion-mode switches are not handled here at
/// all: they only ever appear inside vendor start/layer/filament-change G-code hooks, and the
/// caller (<c>PinnedOrcaCliRotationTests</c>) neutralizes every such hook out of the profile before
/// the real binary ever runs, so no G-code this reader sees can legally contain one.
/// </remarks>
internal static class OrcaGcodeOrientationReader
{
    /// <summary>
    /// Computes the extruding-move bounding-box size from a G-code file's lines.
    /// </summary>
    /// <param name="lines">The G-code file's lines, in order.</param>
    /// <returns>The axis-aligned extent of every extruding move.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the G-code switches to relative positioning, or contains no extruding moves.
    /// </exception>
    internal static GcodeExtent ComputeExtrusionExtent(IEnumerable<string> lines)
    {
        double x = 0, y = 0, z = 0;
        double? lastE = null;
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        bool sawAnyExtrusion = false;

        foreach (string rawLine in lines)
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            string command = tokens[0].ToUpperInvariant();
            if (command is "G91")
            {
                throw new InvalidOperationException(
                    "The G-code switched to relative positioning (G91), which this reader does not " +
                    "support; every profile this suite selects must emit absolute XY positioning.");
            }

            if (command is "G92")
            {
                // G92 re-defines the machine's current position without moving it — most commonly
                // "G92 E0" between objects/layers to reset the extruder axis and avoid floating-point
                // drift. It must rebase lastE to the declared value rather than being ignored,
                // otherwise the next real printing move's E value looks smaller than the pre-reset
                // lastE and is wrongly classified as non-extruding, under-measuring the bounding box.
                (double? resetX, double? resetY, double? resetZ, double? resetE) = ParseAxes(tokens);
                if (resetX.HasValue)
                {
                    x = resetX.Value;
                }

                if (resetY.HasValue)
                {
                    y = resetY.Value;
                }

                if (resetZ.HasValue)
                {
                    z = resetZ.Value;
                }

                if (resetE.HasValue)
                {
                    lastE = resetE.Value;
                }

                continue;
            }

            if (command is not ("G0" or "G1"))
            {
                continue;
            }

            (double? newX, double? newY, double? newZ, double? newE) = ParseAxes(tokens);

            if (newX.HasValue)
            {
                x = newX.Value;
            }

            if (newY.HasValue)
            {
                y = newY.Value;
            }

            if (newZ.HasValue)
            {
                z = newZ.Value;
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }

            bool isExtruding = newE.HasValue && (lastE is null || newE.Value > lastE.Value + 1e-6);
            if (newE.HasValue)
            {
                lastE = newE;
            }

            if (!isExtruding)
            {
                continue;
            }

            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            sawAnyExtrusion = true;
        }

        if (!sawAnyExtrusion)
        {
            throw new InvalidOperationException("The G-code contained no extruding moves to measure.");
        }

        double sizeZ = minZ == double.MaxValue || maxZ == double.MinValue ? 0 : maxZ - minZ;
        return new GcodeExtent(maxX - minX, maxY - minY, sizeZ);
    }

    private static (double? X, double? Y, double? Z, double? E) ParseAxes(string[] tokens)
    {
        double? newX = null, newY = null, newZ = null, newE = null;
        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token.Length < 2)
            {
                continue;
            }

            char axis = char.ToUpperInvariant(token[0]);
            if (axis is not ('X' or 'Y' or 'Z' or 'E') ||
                !double.TryParse(
                    token.AsSpan(1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value))
            {
                continue;
            }

            switch (axis)
            {
                case 'X':
                    newX = value;
                    break;
                case 'Y':
                    newY = value;
                    break;
                case 'Z':
                    newZ = value;
                    break;
                case 'E':
                    newE = value;
                    break;
            }
        }

        return (newX, newY, newZ, newE);
    }

    private static string StripComment(string line)
    {
        int index = line.IndexOf(';', StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }
}
