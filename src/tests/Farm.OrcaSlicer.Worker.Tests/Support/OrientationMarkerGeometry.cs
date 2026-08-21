using System.Buffers.Binary;

namespace Farm.OrcaSlicer.Worker.Tests.Support;

/// <summary>
/// A small, deliberately asymmetric marker solid (an irregular tetrahedron) used by the real
/// -OrcaSlicer-CLI rotation test (<c>PinnedOrcaCliRotationTests</c> in
/// <c>Farm.Web.IntegrationTests</c>, issue #1802) to give the sliced object a shape whose
/// axis-aligned bounding box actually depends on the SIGNED entries of the applied rotation
/// matrix, not merely their absolute values.
/// </summary>
/// <remarks>
/// A symmetric shape (for example a plain box with vertices at ± offsets from its centre) would
/// have a bounding box that depends only on <c>abs(R_ij)</c> of the rotation matrix, which can mask
/// sign or composition errors. This tetrahedron's vertices are not symmetric about the origin, so
/// its bounding box after rotation is sensitive to the real bugs the real-CLI test exists to catch:
/// a reverted negative-Z correction in <c>ToOrcaRotation</c>, or a regression back to emitting the
/// workspace's raw Euler angles verbatim (see
/// <c>OrcaSlicingPipelineService.ToOrcaRotation</c>/<c>BuildTransformFlags</c>).
/// </remarks>
internal static class OrientationMarkerGeometry
{
    /// <summary>Marker footprint along local X, millimetres.</summary>
    internal const double LengthX = 40.0;

    /// <summary>Marker footprint along local Y, millimetres.</summary>
    internal const double WidthY = 24.0;

    /// <summary>Marker footprint along local Z, millimetres.</summary>
    internal const double HeightZ = 12.0;

    /// <summary>The four marker vertices in local mesh space, not symmetric about the origin.</summary>
    internal static readonly (double X, double Y, double Z)[] Vertices =
    [
        (0, 0, 0),
        (LengthX, 0, 0),
        (0, WidthY, 0),
        (0, 0, HeightZ),
    ];

    /// <summary>
    /// The four triangular faces of the tetrahedron, each wound so its normal points outward
    /// (verified analytically via the right-hand rule against the solid's interior).
    /// </summary>
    private static readonly (int A, int B, int C)[] Triangles =
    [
        (0, 2, 1), // z = 0 base
        (0, 1, 3), // y = 0 face
        (0, 3, 2), // x = 0 face
        (1, 2, 3), // slanted face
    ];

    /// <summary>Writes the marker as a binary STL file at <paramref name="path"/>.</summary>
    /// <param name="path">Destination file path.</param>
    internal static void WriteBinaryStl(string path)
    {
        using FileStream stream = File.Create(path);

        // 80-byte header, unused by OrcaSlicer.
        Span<byte> header = stackalloc byte[80];
        stream.Write(header);

        Span<byte> countBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(countBuffer, (uint)Triangles.Length);
        stream.Write(countBuffer);

        Span<byte> attribute = stackalloc byte[2];
        Span<byte> vectorBuffer = stackalloc byte[12];
        foreach ((int a, int b, int c) in Triangles)
        {
            (double X, double Y, double Z) v0 = Vertices[a];
            (double X, double Y, double Z) v1 = Vertices[b];
            (double X, double Y, double Z) v2 = Vertices[c];
            (double X, double Y, double Z) normal = FaceNormal(v0, v1, v2);

            WriteVector(stream, vectorBuffer, normal);
            WriteVector(stream, vectorBuffer, v0);
            WriteVector(stream, vectorBuffer, v1);
            WriteVector(stream, vectorBuffer, v2);

            // 2-byte "attribute byte count", always zero for a plain mesh.
            attribute.Clear();
            stream.Write(attribute);
        }
    }

    /// <summary>
    /// Computes the axis-aligned bounding-box "size" (max minus min, per axis) the marker would
    /// occupy after the viewer applies rotation <c>R = Rx·Ry·Rz</c> (three.js Euler order
    /// <c>'XYZ'</c>, column-vector composition) to it, derived purely from the definition of that
    /// matrix.
    /// </summary>
    /// <remarks>
    /// Deliberately independent of <c>ToOrcaRotation</c>/<c>BuildTransformFlags</c>/
    /// <c>ThreeMfProjectBuilder</c> — the production rotation code under test — so a regression in
    /// either implementation produces a real, measurable divergence rather than being hidden by
    /// shared code between "expected" and "actual".
    /// </remarks>
    /// <remarks>
    /// Bounding-box size is translation-invariant: it depends only on the rotation matrix's effect
    /// on the mesh's relative vertex positions, never on the pivot point the rotation is applied
    /// around. This sidesteps needing to replicate OrcaSlicer's exact rotation pivot (mesh origin
    /// vs. bounding-box centre) to compare sizes.
    /// </remarks>
    /// <param name="rx">Viewer rotation about X, radians.</param>
    /// <param name="ry">Viewer rotation about Y, radians.</param>
    /// <param name="rz">Viewer rotation about Z, radians.</param>
    /// <returns>The expected axis-aligned size after rotation.</returns>
    internal static (double X, double Y, double Z) ComputeExpectedSize(double rx, double ry, double rz)
    {
        double cosX = Math.Cos(rx), sinX = Math.Sin(rx);
        double cosY = Math.Cos(ry), sinY = Math.Sin(ry);
        double cosZ = Math.Cos(rz), sinZ = Math.Sin(rz);

        // R = Rx * Ry * Rz, full matrix (three.js Matrix4.makeRotationFromEuler, case 'XYZ').
        double r00 = cosY * cosZ;
        double r01 = -cosY * sinZ;
        double r02 = sinY;
        double r10 = (cosX * sinZ) + (sinX * sinY * cosZ);
        double r11 = (cosX * cosZ) - (sinX * sinY * sinZ);
        double r12 = -sinX * cosY;
        double r20 = (sinX * sinZ) - (cosX * sinY * cosZ);
        double r21 = (sinX * cosZ) + (cosX * sinY * sinZ);
        double r22 = cosX * cosY;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        foreach ((double X, double Y, double Z) vertex in Vertices)
        {
            double x = (r00 * vertex.X) + (r01 * vertex.Y) + (r02 * vertex.Z);
            double y = (r10 * vertex.X) + (r11 * vertex.Y) + (r12 * vertex.Z);
            double z = (r20 * vertex.X) + (r21 * vertex.Y) + (r22 * vertex.Z);

            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
        }

        return (maxX - minX, maxY - minY, maxZ - minZ);
    }

    private static (double X, double Y, double Z) FaceNormal(
        (double X, double Y, double Z) v0,
        (double X, double Y, double Z) v1,
        (double X, double Y, double Z) v2)
    {
        (double X, double Y, double Z) u = (v1.X - v0.X, v1.Y - v0.Y, v1.Z - v0.Z);
        (double X, double Y, double Z) v = (v2.X - v0.X, v2.Y - v0.Y, v2.Z - v0.Z);
        (double X, double Y, double Z) cross = (
            (u.Y * v.Z) - (u.Z * v.Y),
            (u.Z * v.X) - (u.X * v.Z),
            (u.X * v.Y) - (u.Y * v.X));
        double length = Math.Sqrt((cross.X * cross.X) + (cross.Y * cross.Y) + (cross.Z * cross.Z));
        return length > 0
            ? (cross.X / length, cross.Y / length, cross.Z / length)
            : cross;
    }

    private static void WriteVector(Stream stream, Span<byte> buffer, (double X, double Y, double Z) vector)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer[..4], (float)vector.X);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[4..8], (float)vector.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[8..], (float)vector.Z);
        stream.Write(buffer);
    }
}
