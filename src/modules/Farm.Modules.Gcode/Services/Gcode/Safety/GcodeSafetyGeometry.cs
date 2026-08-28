using Farm.Web.Api.Services.Gcode.Safety;

namespace Farm.Modules.Gcode.Services.Gcode.Safety;

/// <summary>
/// Deterministic decimal planar geometry used to keep every emitted coordinate inside the
/// authoritative printable area and outside every excluded region.
/// </summary>
/// <remarks>
/// Decimal arithmetic is used deliberately: the same inputs must produce byte-identical decisions on
/// every machine, which binary floating point cannot guarantee. Ported from the calibration-scoped
/// <c>CalibrationGeometry</c> helper so the general g-code safety pass has no dependency on any
/// calibration type.
/// </remarks>
public static class GcodeSafetyGeometry
{
    /// <summary>Determines whether a point lies inside or on a closed polygon.</summary>
    /// <param name="polygon">The closed polygon vertices, in authored order.</param>
    /// <param name="x">Point X coordinate.</param>
    /// <param name="y">Point Y coordinate.</param>
    /// <returns><see langword="true"/> when the point is inside or on the boundary.</returns>
    public static bool ContainsPoint(
        IReadOnlyList<GcodeSafetyPoint> polygon,
        decimal x,
        decimal y)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
        {
            return false;
        }

        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            GcodeSafetyPoint current = polygon[i];
            GcodeSafetyPoint previous = polygon[j];

            if (IsOnSegment(previous, current, x, y))
            {
                return true;
            }

            bool straddles = (current.Y > y) != (previous.Y > y);
            if (!straddles)
            {
                continue;
            }

            decimal deltaY = previous.Y - current.Y;
            if (deltaY == 0m)
            {
                continue;
            }

            decimal intersectX = current.X + (((y - current.Y) * (previous.X - current.X)) / deltaY);
            if (x < intersectX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool IsOnSegment(
        GcodeSafetyPoint start,
        GcodeSafetyPoint end,
        decimal x,
        decimal y)
    {
        decimal cross = ((end.X - start.X) * (y - start.Y)) - ((end.Y - start.Y) * (x - start.X));
        if (cross != 0m)
        {
            return false;
        }

        return x >= Math.Min(start.X, end.X) &&
            x <= Math.Max(start.X, end.X) &&
            y >= Math.Min(start.Y, end.Y) &&
            y <= Math.Max(start.Y, end.Y);
    }
}
