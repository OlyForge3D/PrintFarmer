namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Deterministic decimal planar geometry used to keep every emitted coordinate inside the
/// authoritative printable area and outside every excluded region.
/// </summary>
/// <remarks>
/// Decimal arithmetic is used deliberately: the same inputs must produce byte-identical decisions and
/// byte-identical G-code on every machine, which binary floating point cannot guarantee.
/// </remarks>
public static class CalibrationGeometry
{
    /// <summary>Determines whether a point lies inside or on a closed polygon.</summary>
    /// <param name="polygon">The closed polygon vertices, in authored order.</param>
    /// <param name="x">Point X coordinate.</param>
    /// <param name="y">Point Y coordinate.</param>
    /// <returns><see langword="true"/> when the point is inside or on the boundary.</returns>
    public static bool ContainsPoint(
        IReadOnlyList<CalibrationBedPoint> polygon,
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
            CalibrationBedPoint current = polygon[i];
            CalibrationBedPoint previous = polygon[j];

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

    /// <summary>Determines whether an axis-aligned rectangle lies entirely inside a polygon.</summary>
    /// <param name="polygon">The closed polygon vertices, in authored order.</param>
    /// <param name="minX">Rectangle minimum X.</param>
    /// <param name="minY">Rectangle minimum Y.</param>
    /// <param name="maxX">Rectangle maximum X.</param>
    /// <param name="maxY">Rectangle maximum Y.</param>
    /// <returns><see langword="true"/> when every sampled rectangle point is inside the polygon.</returns>
    /// <remarks>
    /// The rectangle is sampled at its corners, edge midpoints and centre. A convex printable polygon,
    /// which is what printer configurations describe, is fully covered by those samples.
    /// </remarks>
    public static bool ContainsRectangle(
        IReadOnlyList<CalibrationBedPoint> polygon,
        decimal minX,
        decimal minY,
        decimal maxX,
        decimal maxY)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        foreach ((decimal x, decimal y) in SampleRectangle(minX, minY, maxX, maxY))
        {
            if (!ContainsPoint(polygon, x, y))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Determines whether an axis-aligned rectangle touches a polygon at all.</summary>
    /// <param name="polygon">The closed polygon vertices, in authored order.</param>
    /// <param name="minX">Rectangle minimum X.</param>
    /// <param name="minY">Rectangle minimum Y.</param>
    /// <param name="maxX">Rectangle maximum X.</param>
    /// <param name="maxY">Rectangle maximum Y.</param>
    /// <returns><see langword="true"/> when the rectangle and polygon overlap.</returns>
    public static bool IntersectsRectangle(
        IReadOnlyList<CalibrationBedPoint> polygon,
        decimal minX,
        decimal minY,
        decimal maxX,
        decimal maxY)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        foreach ((decimal x, decimal y) in SampleRectangle(minX, minY, maxX, maxY))
        {
            if (ContainsPoint(polygon, x, y))
            {
                return true;
            }
        }

        foreach (CalibrationBedPoint vertex in polygon)
        {
            if (vertex.X >= minX && vertex.X <= maxX && vertex.Y >= minY && vertex.Y <= maxY)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the axis-aligned rectangle polygon of a build volume with its origin offset.</summary>
    /// <param name="bed">The authoritative bed geometry.</param>
    /// <returns>The bed rectangle as a closed polygon, or an empty list when extents are missing.</returns>
    public static IReadOnlyList<CalibrationBedPoint> BuildVolumeRectangle(CalibrationBedGeometry bed)
    {
        ArgumentNullException.ThrowIfNull(bed);
        if (bed.SizeXMillimeters is not { } sizeX || bed.SizeYMillimeters is not { } sizeY)
        {
            return [];
        }

        decimal originX = bed.OriginXMillimeters ?? 0m;
        decimal originY = bed.OriginYMillimeters ?? 0m;
        return
        [
            new CalibrationBedPoint(originX, originY),
            new CalibrationBedPoint(originX + sizeX, originY),
            new CalibrationBedPoint(originX + sizeX, originY + sizeY),
            new CalibrationBedPoint(originX, originY + sizeY),
        ];
    }

    private static IEnumerable<(decimal X, decimal Y)> SampleRectangle(
        decimal minX,
        decimal minY,
        decimal maxX,
        decimal maxY)
    {
        decimal midX = (minX + maxX) / 2m;
        decimal midY = (minY + maxY) / 2m;
        yield return (minX, minY);
        yield return (maxX, minY);
        yield return (maxX, maxY);
        yield return (minX, maxY);
        yield return (midX, minY);
        yield return (midX, maxY);
        yield return (minX, midY);
        yield return (maxX, midY);
        yield return (midX, midY);
    }

    private static bool IsOnSegment(
        CalibrationBedPoint start,
        CalibrationBedPoint end,
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
