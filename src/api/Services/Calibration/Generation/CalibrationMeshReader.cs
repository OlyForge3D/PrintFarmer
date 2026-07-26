using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Reads canonical STL and 3MF content with hard structural, archive and XML safety budgets.
/// </summary>
/// <remarks>
/// The reader is deliberately self-contained rather than delegating to a general mesh library. It must
/// reject hostile input before any allocation grows with attacker-controlled numbers, must never
/// resolve an external XML entity or DTD, and must never rearrange 3MF assembly components: build item
/// transforms are applied exactly as authored so calibration geometry keeps its intended placement.
/// </remarks>
internal static class CalibrationMeshReader
{
    /// <summary>Maximum accepted content size, in bytes.</summary>
    public const long MaxContentBytes = 64L * 1024 * 1024;

    /// <summary>Maximum accepted triangle count across all objects.</summary>
    public const int MaxTriangles = 2_000_000;

    /// <summary>Maximum accepted printable object count.</summary>
    public const int MaxObjects = 64;

    /// <summary>Maximum accepted archive entry count.</summary>
    public const int MaxArchiveEntries = 256;

    /// <summary>Maximum accepted total uncompressed archive size, in bytes.</summary>
    public const long MaxArchiveUncompressedBytes = 256L * 1024 * 1024;

    /// <summary>Maximum accepted archive compression ratio before the input is treated as a bomb.</summary>
    public const long MaxArchiveCompressionRatio = 200;

    /// <summary>Maximum accepted XML nesting depth inside a 3MF model part.</summary>
    public const int MaxXmlDepth = 64;

    private const string CoreNamespace2015 =
        "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    private static readonly string[] AllowedArchivePrefixes =
    [
        "3d/",
        "_rels/",
        "metadata/",
        "[content_types].xml",
    ];

    /// <summary>The outcome of a mesh read: geometry, or a structured rejection.</summary>
    /// <param name="ObjectCount">Number of printable objects.</param>
    /// <param name="TriangleCount">Number of triangles.</param>
    /// <param name="Bounds">Bounds computed from actual geometry.</param>
    /// <param name="Unit">Declared unit.</param>
    /// <param name="Problem">The rejection, when the read failed.</param>
    public sealed record MeshReadResult(
        int ObjectCount,
        int TriangleCount,
        CalibrationModelBounds? Bounds,
        string Unit,
        CalibrationGenerationProblem? Problem);

    /// <summary>Reads canonical STL bytes and computes actual bounds.</summary>
    /// <param name="content">The STL content.</param>
    /// <param name="field">Dotted field path used in any rejection.</param>
    /// <returns>The read result.</returns>
    public static MeshReadResult ReadStl(ReadOnlySpan<byte> content, string field)
    {
        if (content.Length < 15)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The model content is too small to be a valid mesh.");
        }

        if (content.Length > MaxContentBytes)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelTooLarge,
                field,
                "The model content exceeds the accepted size.");
        }

        return LooksLikeAsciiStl(content)
            ? ReadAsciiStl(content, field)
            : ReadBinaryStl(content, field);
    }

    /// <summary>Reads a 3MF package and computes actual bounds without rearranging components.</summary>
    /// <param name="content">The 3MF package content.</param>
    /// <param name="field">Dotted field path used in any rejection.</param>
    /// <returns>The read result.</returns>
    public static MeshReadResult ReadThreeMf(byte[] content, string field)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.LongLength > MaxContentBytes)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelTooLarge,
                field,
                "The model content exceeds the accepted size.");
        }

        using MemoryStream stream = new(content, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The model package is not a readable archive.");
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                return Fail(
                    CalibrationGenerationProblemCodes.ModelResourceLimitExceeded,
                    field,
                    "The model package declares too many entries.");
            }

            long totalUncompressed = 0;
            long totalCompressed = 0;
            ZipArchiveEntry? modelEntry = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsSafeEntryName(entry.FullName))
                {
                    return Fail(
                        CalibrationGenerationProblemCodes.ModelArchivePathTraversal,
                        field,
                        "The model package contains an unsafe entry name.");
                }

                if (!IsAllowedResource(entry.FullName))
                {
                    return Fail(
                        CalibrationGenerationProblemCodes.ModelArchiveUnsupportedResource,
                        field,
                        "The model package contains an unsupported resource.");
                }

                totalUncompressed += entry.Length;
                totalCompressed += entry.CompressedLength;
                if (totalUncompressed > MaxArchiveUncompressedBytes)
                {
                    return Fail(
                        CalibrationGenerationProblemCodes.ModelArchiveDecompressionBomb,
                        field,
                        "The model package expands beyond the accepted budget.");
                }

                if (entry.FullName.EndsWith("3dmodel.model", StringComparison.OrdinalIgnoreCase))
                {
                    modelEntry ??= entry;
                }
            }

            if (totalCompressed > 0 &&
                totalUncompressed / Math.Max(totalCompressed, 1) > MaxArchiveCompressionRatio)
            {
                return Fail(
                    CalibrationGenerationProblemCodes.ModelArchiveDecompressionBomb,
                    field,
                    "The model package compression ratio indicates a decompression bomb.");
            }

            if (modelEntry is null)
            {
                return Fail(
                    CalibrationGenerationProblemCodes.ModelContentInvalid,
                    field,
                    "The model package does not contain a 3D model part.");
            }

            return ReadThreeMfModelPart(modelEntry, field);
        }
    }

    private static MeshReadResult ReadThreeMfModelPart(ZipArchiveEntry entry, string field)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = true,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxArchiveUncompressedBytes,
        };

        string unit = "millimeter";
        int objectCount = 0;
        int triangleCount = 0;
        decimal minX = decimal.MaxValue;
        decimal minY = decimal.MaxValue;
        decimal minZ = decimal.MaxValue;
        decimal maxX = decimal.MinValue;
        decimal maxY = decimal.MinValue;
        decimal maxZ = decimal.MinValue;
        List<(decimal X, decimal Y, decimal Z)> vertices = [];
        bool sawMesh = false;

        try
        {
            using Stream partStream = entry.Open();
            using XmlReader reader = XmlReader.Create(partStream, settings);
            while (reader.Read())
            {
                if (reader.Depth > MaxXmlDepth)
                {
                    return Fail(
                        CalibrationGenerationProblemCodes.ModelArchiveXmlUnsafe,
                        field,
                        "The model part XML is nested beyond the accepted depth.");
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "model":
                        if (!string.IsNullOrEmpty(reader.NamespaceURI) &&
                            !string.Equals(reader.NamespaceURI, CoreNamespace2015, StringComparison.Ordinal))
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelArchiveUnsupportedResource,
                                field,
                                "The model part declares an unsupported core namespace.");
                        }

                        unit = reader.GetAttribute("unit") ?? "millimeter";
                        if (!string.Equals(unit, "millimeter", StringComparison.Ordinal))
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelUnitUnsupported,
                                field,
                                "Calibration models must declare millimetre units.");
                        }

                        break;
                    case "object":
                        objectCount++;
                        if (objectCount > MaxObjects)
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelResourceLimitExceeded,
                                field,
                                "The model declares too many objects.");
                        }

                        vertices.Clear();
                        break;
                    case "mesh":
                        sawMesh = true;
                        break;
                    case "vertex":
                        if (!TryReadVertex(reader, out (decimal X, decimal Y, decimal Z) vertex))
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelContentInvalid,
                                field,
                                "The model part contains a malformed vertex.");
                        }

                        vertices.Add(vertex);
                        if (vertices.Count > MaxTriangles)
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelResourceLimitExceeded,
                                field,
                                "The model declares too many vertices.");
                        }

                        Accumulate(vertex, ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
                        break;
                    case "triangle":
                        triangleCount++;
                        if (triangleCount > MaxTriangles)
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelResourceLimitExceeded,
                                field,
                                "The model declares too many triangles.");
                        }

                        break;
                    case "item":
                    case "component":
                        string? transform = reader.GetAttribute("transform");
                        if (!string.IsNullOrWhiteSpace(transform) && !IsSupportedTransform(transform))
                        {
                            return Fail(
                                CalibrationGenerationProblemCodes.ModelTransformUnsupported,
                                field,
                                "The model declares an unsupported component transform.");
                        }

                        break;
                    default:
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelArchiveXmlUnsafe,
                field,
                "The model part XML is malformed or unsafe.");
        }
        catch (InvalidDataException)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The model package could not be decompressed.");
        }

        if (!sawMesh || triangleCount == 0 || maxX <= decimal.MinValue)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The model part declares no printable mesh.");
        }

        return new MeshReadResult(
            Math.Max(objectCount, 1),
            triangleCount,
            new CalibrationModelBounds(minX, minY, minZ, maxX, maxY, maxZ),
            unit,
            null);
    }

    private static bool TryReadVertex(XmlReader reader, out (decimal X, decimal Y, decimal Z) vertex)
    {
        vertex = default;
        if (!TryParseCoordinate(reader.GetAttribute("x"), out decimal x) ||
            !TryParseCoordinate(reader.GetAttribute("y"), out decimal y) ||
            !TryParseCoordinate(reader.GetAttribute("z"), out decimal z))
        {
            return false;
        }

        vertex = (x, y, z);
        return true;
    }

    private static bool TryParseCoordinate(string? value, out decimal coordinate)
    {
        coordinate = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double parsed) ||
            double.IsNaN(parsed) ||
            double.IsInfinity(parsed) ||
            Math.Abs(parsed) > 100_000d)
        {
            return false;
        }

        coordinate = decimal.Round((decimal)parsed, 4);
        return true;
    }

    private static bool IsSupportedTransform(string transform)
    {
        string[] parts = transform.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 12)
        {
            return false;
        }

        foreach (string part in parts)
        {
            if (!double.TryParse(
                part,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value) ||
                double.IsNaN(value) ||
                double.IsInfinity(value) ||
                Math.Abs(value) > 100_000d)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.StartsWith('/') ||
            name.StartsWith('\\') ||
            name.Contains(':', StringComparison.Ordinal) ||
            name.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        return !Path.IsPathRooted(name);
    }

    private static bool IsAllowedResource(string name)
    {
        string normalized = name.Replace('\\', '/').ToLowerInvariant();
        if (normalized.EndsWith('/'))
        {
            return true;
        }

        foreach (string prefix in AllowedArchivePrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal) ||
                string.Equals(normalized, prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeAsciiStl(ReadOnlySpan<byte> content)
    {
        ReadOnlySpan<byte> header = content[..Math.Min(6, content.Length)];
        if (!header.StartsWith("solid"u8))
        {
            return false;
        }

        // A binary STL may also begin with "solid". Trust the declared triangle count instead: a
        // binary file's size is exactly 84 + 50 * triangleCount.
        if (content.Length < 84)
        {
            return true;
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(80, 4));
        long expected = 84L + (50L * declared);
        return expected != content.Length;
    }

    private static MeshReadResult ReadBinaryStl(ReadOnlySpan<byte> content, string field)
    {
        if (content.Length < 84)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The model content is not a valid binary mesh.");
        }

        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(80, 4));
        if (triangleCount == 0 || triangleCount > MaxTriangles)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelResourceLimitExceeded,
                field,
                "The model declares an unsupported triangle count.");
        }

        long expected = 84L + (50L * triangleCount);
        if (expected != content.Length)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The declared triangle count does not match the model content length.");
        }

        decimal minX = decimal.MaxValue;
        decimal minY = decimal.MaxValue;
        decimal minZ = decimal.MaxValue;
        decimal maxX = decimal.MinValue;
        decimal maxY = decimal.MinValue;
        decimal maxZ = decimal.MinValue;

        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int recordOffset = 84 + (triangle * 50);
            for (int vertexIndex = 0; vertexIndex < 3; vertexIndex++)
            {
                int offset = recordOffset + 12 + (vertexIndex * 12);
                float x = BinaryPrimitives.ReadSingleLittleEndian(content.Slice(offset, 4));
                float y = BinaryPrimitives.ReadSingleLittleEndian(content.Slice(offset + 4, 4));
                float z = BinaryPrimitives.ReadSingleLittleEndian(content.Slice(offset + 8, 4));
                if (!TryConvert(x, out decimal dx) ||
                    !TryConvert(y, out decimal dy) ||
                    !TryConvert(z, out decimal dz))
                {
                    return Fail(
                        CalibrationGenerationProblemCodes.ModelContentInvalid,
                        field,
                        "The model contains a non-finite or out-of-range vertex.");
                }

                Accumulate((dx, dy, dz), ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
            }
        }

        return new MeshReadResult(
            1,
            (int)triangleCount,
            new CalibrationModelBounds(minX, minY, minZ, maxX, maxY, maxZ),
            "millimeter",
            null);
    }

    private static MeshReadResult ReadAsciiStl(ReadOnlySpan<byte> content, string field)
    {
        string text = Encoding.UTF8.GetString(content);
        decimal minX = decimal.MaxValue;
        decimal minY = decimal.MaxValue;
        decimal minZ = decimal.MaxValue;
        decimal maxX = decimal.MinValue;
        decimal maxY = decimal.MinValue;
        decimal maxZ = decimal.MinValue;
        int vertexCount = 0;

        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4 ||
                !TryParseCoordinate(parts[1], out decimal x) ||
                !TryParseCoordinate(parts[2], out decimal y) ||
                !TryParseCoordinate(parts[3], out decimal z))
            {
                return Fail(
                    CalibrationGenerationProblemCodes.ModelContentInvalid,
                    field,
                    "The model contains a malformed vertex.");
            }

            vertexCount++;
            if (vertexCount > MaxTriangles * 3)
            {
                return Fail(
                    CalibrationGenerationProblemCodes.ModelResourceLimitExceeded,
                    field,
                    "The model declares too many vertices.");
            }

            Accumulate((x, y, z), ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ);
        }

        if (vertexCount == 0 || vertexCount % 3 != 0)
        {
            return Fail(
                CalibrationGenerationProblemCodes.ModelContentInvalid,
                field,
                "The model declares no complete triangles.");
        }

        return new MeshReadResult(
            1,
            vertexCount / 3,
            new CalibrationModelBounds(minX, minY, minZ, maxX, maxY, maxZ),
            "millimeter",
            null);
    }

    private static bool TryConvert(float value, out decimal converted)
    {
        converted = 0m;
        if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 100_000f)
        {
            return false;
        }

        converted = decimal.Round((decimal)value, 4);
        return true;
    }

    private static void Accumulate(
        (decimal X, decimal Y, decimal Z) vertex,
        ref decimal minX,
        ref decimal minY,
        ref decimal minZ,
        ref decimal maxX,
        ref decimal maxY,
        ref decimal maxZ)
    {
        minX = Math.Min(minX, vertex.X);
        minY = Math.Min(minY, vertex.Y);
        minZ = Math.Min(minZ, vertex.Z);
        maxX = Math.Max(maxX, vertex.X);
        maxY = Math.Max(maxY, vertex.Y);
        maxZ = Math.Max(maxZ, vertex.Z);
    }

    private static MeshReadResult Fail(string code, string field, string message) =>
        new(0, 0, null, string.Empty, new CalibrationGenerationProblem(code, field, message));
}
