using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Builds a 3MF project file embedding multiple STL meshes with per-model
/// affine transforms. OrcaSlicer opens the resulting .3mf directly, preserving
/// each model's position/rotation/scale from the UI workspace.
/// </summary>
internal static class ThreeMfProjectBuilder
{
    private static readonly XNamespace Ns3Mf = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace NsContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace NsRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Threshold below which floating-point matrix values are snapped to zero.</summary>
    private const double CleanEpsilon = 1e-12;

    internal readonly record struct ModelEntry(string FilePath, string? TransformJson);

    internal readonly record struct MeshData(
        IReadOnlyList<(float X, float Y, float Z)> Vertices,
        IReadOnlyList<(int V1, int V2, int V3)> Triangles);

    /// <summary>
    /// Creates a 3MF file in <paramref name="outputDirectory"/> embedding the given models
    /// with their transforms baked into the 3MF build section.
    /// </summary>
    /// <returns>Absolute path to the generated .3mf file.</returns>
    internal static string Build(IReadOnlyList<ModelEntry> models, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            throw new ArgumentException("At least one model is required.", nameof(models));
        }

        string outputPath = Path.Combine(outputDirectory, "project.3mf");

        var meshes = new List<MeshData>(models.Count);
        foreach (ModelEntry model in models)
        {
            meshes.Add(ParseBinaryStl(model.FilePath));
        }

        XDocument modelDoc = BuildModelXml(models, meshes);

        using FileStream fs = File.Create(outputPath);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);

        WriteContentTypes(zip);
        WriteRelationships(zip);

        ZipArchiveEntry entry = zip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        {
            modelDoc.Save(stream);
        }

        return outputPath;
    }

    /// <summary>
    /// Parse a binary STL file into deduplicated vertices and triangle indices.
    /// </summary>
    internal static MeshData ParseBinaryStl(string filePath)
    {
        byte[] data = File.ReadAllBytes(filePath);
        if (data.Length < 84)
        {
            throw new InvalidOperationException($"STL file too small ({data.Length} bytes): {filePath}");
        }

        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80, 4));
        long expectedSize = 84 + ((long)triangleCount * 50);
        if (data.Length < expectedSize)
        {
            throw new InvalidOperationException(
                $"STL file size mismatch: expected at least {expectedSize} bytes for {triangleCount} triangles, got {data.Length}");
        }

        var vertexMap = new Dictionary<(float, float, float), int>();
        var vertices = new List<(float X, float Y, float Z)>();
        var triangles = new List<(int V1, int V2, int V3)>((int)triangleCount);

        int offset = 84;
        for (uint t = 0; t < triangleCount; t++)
        {
            offset += 12; // skip normal vector
            int v1 = ReadAndDeduplicateVertex(data, ref offset, vertexMap, vertices);
            int v2 = ReadAndDeduplicateVertex(data, ref offset, vertexMap, vertices);
            int v3 = ReadAndDeduplicateVertex(data, ref offset, vertexMap, vertices);
            triangles.Add((v1, v2, v3));
            offset += 2; // skip attribute byte count
        }

        return new MeshData(vertices, triangles);
    }

    /// <summary>
    /// Parse transform JSON and return a 3MF transform attribute string (12 space-separated floats).
    /// Returns empty string for null/identity transforms.
    /// </summary>
    internal static string BuildTransformAttribute(string? transformJson)
    {
        if (string.IsNullOrWhiteSpace(transformJson))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(transformJson);
            JsonElement root = doc.RootElement;

            double[] rot = [0, 0, 0];
            double[] scale = [1, 1, 1];
            double[] pos = [0, 0, 0];

            if (root.TryGetProperty("rotation", out JsonElement rotEl) && rotEl.ValueKind == JsonValueKind.Array)
            {
                ParseArrayInto(rotEl, rot);
            }

            if (root.TryGetProperty("scale", out JsonElement scaleEl) && scaleEl.ValueKind == JsonValueKind.Array)
            {
                ParseArrayInto(scaleEl, scale);
            }

            if (root.TryGetProperty("position", out JsonElement posEl) && posEl.ValueKind == JsonValueKind.Array)
            {
                ParseArrayInto(posEl, pos);
            }

            const double eps = 0.0001;
            bool isIdentity = Math.Abs(rot[0]) < eps && Math.Abs(rot[1]) < eps && Math.Abs(rot[2]) < eps
                           && Math.Abs(scale[0] - 1) < eps && Math.Abs(scale[1] - 1) < eps && Math.Abs(scale[2] - 1) < eps
                           && Math.Abs(pos[0]) < eps && Math.Abs(pos[1]) < eps && Math.Abs(pos[2]) < eps;

            if (isIdentity)
            {
                return string.Empty;
            }

            return ComputeTransformMatrix(rot[0], rot[1], rot[2], scale[0], scale[1], scale[2], pos[0], pos[1], pos[2]);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Compute the 3MF transform attribute (12 floats) from Euler rotation (radians, XYZ order),
    /// scale, and translation. Coordinate system: Z-up, XY bed plane (matching OrcaSlicer).
    /// <para>
    /// 3MF uses row-vector convention: p' = [x, y, z, 1] × M.
    /// The 12 values are: m00 m01 m02  m10 m11 m12  m20 m21 m22  m30 m31 m32.
    /// </para>
    /// </summary>
    internal static string ComputeTransformMatrix(
        double rx, double ry, double rz,
        double sx, double sy, double sz,
        double tx, double ty, double tz)
    {
        double cosRx = Math.Cos(rx), sinRx = Math.Sin(rx);
        double cosRy = Math.Cos(ry), sinRy = Math.Sin(ry);
        double cosRz = Math.Cos(rz), sinRz = Math.Sin(rz);

        // Combined rotation R = Rx × Ry × Rz (row-vector: apply X first, then Y, then Z).
        // Upper-left 3×3 = Scale × R.
        double m00 = sx * cosRy * cosRz;
        double m01 = sx * cosRy * sinRz;
        double m02 = sx * (-sinRy);

        double m10 = sy * ((sinRx * sinRy * cosRz) - (cosRx * sinRz));
        double m11 = sy * ((sinRx * sinRy * sinRz) + (cosRx * cosRz));
        double m12 = sy * sinRx * cosRy;

        double m20 = sz * ((cosRx * sinRy * cosRz) + (sinRx * sinRz));
        double m21 = sz * ((cosRx * sinRy * sinRz) - (sinRx * cosRz));
        double m22 = sz * cosRx * cosRy;

        return string.Create(Inv, $"{C(m00):G9} {C(m01):G9} {C(m02):G9} {C(m10):G9} {C(m11):G9} {C(m12):G9} {C(m20):G9} {C(m21):G9} {C(m22):G9} {C(tx):G9} {C(ty):G9} {C(tz):G9}");
    }

    /// <summary>Snap near-zero values to exactly zero to avoid scientific notation noise.</summary>
    private static double C(double v) => Math.Abs(v) < CleanEpsilon ? 0.0 : v;

    private static void ParseArrayInto(JsonElement array, double[] values)
    {
        int i = 0;
        foreach (JsonElement el in array.EnumerateArray())
        {
            if (i >= values.Length)
            {
                break;
            }

            if (el.ValueKind == JsonValueKind.Number)
            {
                double v = el.GetDouble();
                if (double.IsFinite(v))
                {
                    values[i] = v;
                }
            }

            i++;
        }
    }

    private static int ReadAndDeduplicateVertex(
        byte[] data,
        ref int offset,
        Dictionary<(float X, float Y, float Z), int> vertexMap,
        List<(float X, float Y, float Z)> vertices)
    {
        float x = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
        float y = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 4));
        float z = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 8));
        offset += 12;

        var key = (x, y, z);
        if (vertexMap.TryGetValue(key, out int index))
        {
            return index;
        }

        index = vertices.Count;
        vertices.Add(key);
        vertexMap[key] = index;
        return index;
    }

    private static XDocument BuildModelXml(
        IReadOnlyList<ModelEntry> models,
        List<MeshData> meshes)
    {
        var resources = new XElement(Ns3Mf + "resources");
        var buildSection = new XElement(Ns3Mf + "build");

        for (int i = 0; i < models.Count; i++)
        {
            int objectId = i + 1;
            MeshData mesh = meshes[i];

            var verticesEl = new XElement(Ns3Mf + "vertices");
            foreach (var (x, y, z) in mesh.Vertices)
            {
                verticesEl.Add(new XElement(
                    Ns3Mf + "vertex",
                    new XAttribute("x", x.ToString("G9", Inv)),
                    new XAttribute("y", y.ToString("G9", Inv)),
                    new XAttribute("z", z.ToString("G9", Inv))));
            }

            var trianglesEl = new XElement(Ns3Mf + "triangles");
            foreach (var (v1, v2, v3) in mesh.Triangles)
            {
                trianglesEl.Add(new XElement(
                    Ns3Mf + "triangle",
                    new XAttribute("v1", v1),
                    new XAttribute("v2", v2),
                    new XAttribute("v3", v3)));
            }

            resources.Add(new XElement(
                Ns3Mf + "object",
                new XAttribute("id", objectId),
                new XAttribute("type", "model"),
                new XElement(Ns3Mf + "mesh", verticesEl, trianglesEl)));

            var item = new XElement(
                Ns3Mf + "item",
                new XAttribute("objectid", objectId));

            string transformAttr = BuildTransformAttribute(models[i].TransformJson);
            if (!string.IsNullOrEmpty(transformAttr))
            {
                item.Add(new XAttribute("transform", transformAttr));
            }

            buildSection.Add(item);
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                Ns3Mf + "model",
                new XAttribute("unit", "millimeter"),
                resources,
                buildSection));
    }

    private static void WriteContentTypes(ZipArchive zip)
    {
        ZipArchiveEntry entry = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
        using Stream stream = entry.Open();

        XDocument doc = new(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                NsContentTypes + "Types",
                new XElement(
                    NsContentTypes + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(
                    NsContentTypes + "Default",
                    new XAttribute("Extension", "model"),
                    new XAttribute("ContentType", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml"))));

        doc.Save(stream);
    }

    private static void WriteRelationships(ZipArchive zip)
    {
        ZipArchiveEntry entry = zip.CreateEntry("_rels/.rels", CompressionLevel.Optimal);
        using Stream stream = entry.Open();

        XDocument doc = new(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                NsRelationships + "Relationships",
                new XElement(
                    NsRelationships + "Relationship",
                    new XAttribute("Target", "/3D/3dmodel.model"),
                    new XAttribute("Id", "rel0"),
                    new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"))));

        doc.Save(stream);
    }
}
