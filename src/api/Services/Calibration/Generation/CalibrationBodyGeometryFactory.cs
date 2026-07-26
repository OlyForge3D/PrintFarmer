using System.Buffers.Binary;
using System.Globalization;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Builds the deterministic calibration body the pinned upstream slicer receives.
/// </summary>
/// <remarks>
/// The body is derived only from the compiled specification, so the same specification always produces
/// byte-identical geometry and therefore the same content digest on every host. No caller mesh, archive
/// or renderer is involved: the server emits a closed axis-aligned prism that exactly matches the
/// footprint and segment stack the specification already resolved and validated.
/// </remarks>
public static class CalibrationBodyGeometryFactory
{
    /// <summary>Stable, path-free file name used for the generated body.</summary>
    public const string BodyFileName = "calibration-body.stl";

    private const int StlHeaderBytes = 80;
    private const int StlTriangleBytes = 50;

    /// <summary>
    /// Builds the canonical binary STL body for a compiled specification.
    /// </summary>
    /// <param name="specification">The compiled specification.</param>
    /// <returns>The deterministic geometry, ready for validation.</returns>
    /// <example>
    /// <code>
    /// CalibrationGeneratedGeometry body = CalibrationBodyGeometryFactory.Build(specification);
    /// </code>
    /// </example>
    public static CalibrationGeneratedGeometry Build(CalibrationSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        CalibrationSpecificationDocument document = specification.Document;
        decimal sizeX = document.Footprint.SizeXMillimeters;
        decimal sizeY = document.Footprint.SizeYMillimeters;
        decimal sizeZ = document.Segments.Count == 0
            ? document.Print.FirstLayerHeightMillimeters
            : document.Segments[^1].EndZMillimeters;
        if (sizeZ <= 0m)
        {
            sizeZ = document.Print.FirstLayerHeightMillimeters;
        }

        return new CalibrationGeneratedGeometry(
            BuildPrism((float)sizeX, (float)sizeY, (float)sizeZ),
            BodyFileName);
    }

    /// <summary>
    /// Builds a stable display name for the generated body of one attempt.
    /// </summary>
    /// <param name="attemptId">The immutable attempt identity.</param>
    /// <returns>A safe, path-free display name.</returns>
    public static string BuildStoredModelName(Guid attemptId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"calibration-body-{attemptId:N}.stl");

    private static byte[] BuildPrism(float sizeX, float sizeY, float sizeZ)
    {
        (float X, float Y, float Z)[] corners =
        [
            (0f, 0f, 0f),
            (sizeX, 0f, 0f),
            (sizeX, sizeY, 0f),
            (0f, sizeY, 0f),
            (0f, 0f, sizeZ),
            (sizeX, 0f, sizeZ),
            (sizeX, sizeY, sizeZ),
            (0f, sizeY, sizeZ),
        ];

        int[][] faces =
        [
            [0, 2, 1], [0, 3, 2],
            [4, 5, 6], [4, 6, 7],
            [0, 1, 5], [0, 5, 4],
            [1, 2, 6], [1, 6, 5],
            [2, 3, 7], [2, 7, 6],
            [3, 0, 4], [3, 4, 7],
        ];

        byte[] content = new byte[StlHeaderBytes + 4 + (StlTriangleBytes * faces.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            content.AsSpan(StlHeaderBytes, 4),
            (uint)faces.Length);

        for (int index = 0; index < faces.Length; index++)
        {
            int offset = StlHeaderBytes + 4 + (index * StlTriangleBytes) + 12;
            foreach (int corner in faces[index])
            {
                (float x, float y, float z) = corners[corner];
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset, 4), x);
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset + 4, 4), y);
                BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(offset + 8, 4), z);
                offset += 12;
            }
        }

        return content;
    }
}
