using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// Builds a 3MF project file embedding multiple STL meshes with per-model
/// affine transforms. OrcaSlicer opens the resulting .3mf directly, preserving
/// each model's position/rotation/scale from the UI workspace.
/// </summary>
internal static class ThreeMfProjectBuilder
{
    private const string Ns3MfUri = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
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
    /// Axis-aligned bounds of a raw mesh, used as the pivot for placement.
    /// The workspace viewer recentres every loaded mesh on this box before applying
    /// rotation/scale, so the 3MF transform has to use the same pivot to land the model
    /// where the user put it.
    /// </summary>
    internal readonly record struct MeshBounds(
        double CenterX,
        double CenterY,
        double CenterZ,
        double HalfHeight);

    /// <summary>
    /// Creates a 3MF file in <paramref name="outputDirectory"/> embedding the given models
    /// with their transforms baked into the 3MF build section.
    /// </summary>
    /// <param name="models">Model files plus their workspace transform JSON.</param>
    /// <param name="outputDirectory">Directory to write <c>project.3mf</c> into.</param>
    /// <param name="bedCenter">
    /// Bed centre in OrcaSlicer bed coordinates (the centre of the machine profile's
    /// <c>printable_area</c>). Workspace positions are expressed relative to the bed centre,
    /// while OrcaSlicer places 3MF build items in bed coordinates whose origin is usually the
    /// front-left corner, so every model is translated by this offset.
    /// </param>
    /// <returns>Absolute path to the generated .3mf file.</returns>
    internal static string Build(
        IReadOnlyList<ModelEntry> models,
        string outputDirectory,
        (double X, double Y) bedCenter)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            throw new ArgumentException("At least one model is required.", nameof(models));
        }

        string outputPath = Path.Join(outputDirectory, "project.3mf");

        using FileStream fs = File.Create(outputPath);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);

        WriteContentTypes(zip);
        WriteRelationships(zip);

        ZipArchiveEntry entry = zip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            CloseOutput = false,
        });

        // Written as a stream rather than an XDocument: this path is now the default for any
        // positioned model, and a materialised tree costs roughly an order of magnitude more
        // memory than the STL itself (one element plus attributes per vertex and triangle).
        // Meshes are parsed one at a time for the same reason — only the tiny MeshBounds of
        // each model is retained for the build section.
        writer.WriteStartDocument();
        writer.WriteStartElement("model", Ns3MfUri);
        writer.WriteAttributeString("unit", "millimeter");

        var bounds = new List<MeshBounds>(models.Count);

        writer.WriteStartElement("resources", Ns3MfUri);
        for (int i = 0; i < models.Count; i++)
        {
            MeshData mesh = ParseBinaryStl(models[i].FilePath);
            bounds.Add(ComputeBounds(mesh));
            WriteObject(writer, i + 1, mesh);
        }

        writer.WriteEndElement();

        writer.WriteStartElement("build", Ns3MfUri);
        for (int i = 0; i < models.Count; i++)
        {
            writer.WriteStartElement("item", Ns3MfUri);
            writer.WriteAttributeString("objectid", (i + 1).ToString(Inv));
            writer.WriteAttributeString("transform", BuildItemTransform(models[i].TransformJson, bounds[i], bedCenter));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();

        return outputPath;
    }

    private static void WriteObject(XmlWriter writer, int objectId, MeshData mesh)
    {
        writer.WriteStartElement("object", Ns3MfUri);
        writer.WriteAttributeString("id", objectId.ToString(Inv));
        writer.WriteAttributeString("type", "model");

        writer.WriteStartElement("mesh", Ns3MfUri);

        writer.WriteStartElement("vertices", Ns3MfUri);
        foreach ((float x, float y, float z) in mesh.Vertices)
        {
            writer.WriteStartElement("vertex", Ns3MfUri);
            writer.WriteAttributeString("x", x.ToString("G9", Inv));
            writer.WriteAttributeString("y", y.ToString("G9", Inv));
            writer.WriteAttributeString("z", z.ToString("G9", Inv));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("triangles", Ns3MfUri);
        foreach ((int v1, int v2, int v3) in mesh.Triangles)
        {
            writer.WriteStartElement("triangle", Ns3MfUri);
            writer.WriteAttributeString("v1", v1.ToString(Inv));
            writer.WriteAttributeString("v2", v2.ToString(Inv));
            writer.WriteAttributeString("v3", v3.ToString(Inv));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// Hard ceiling on triangles accepted from a single STL.
    /// <para>
    /// This path used to run only for multi-model jobs with secondary transforms; it is now the
    /// default for any positioned model, so it is reached by ordinary user uploads. Both the
    /// vertex dictionary and the emitted XML scale with this count, so it is bounded explicitly
    /// rather than trusting a 32-bit count read straight out of an untrusted file header.
    /// 5M triangles is ~250 MB of binary STL — far above any realistic print — and exceeding it
    /// throws, which engages the caller's auto-arrange fallback instead of failing the job.
    /// </para>
    /// </summary>
    internal const int MaxTriangles = 5_000_000;

    private const int StlHeaderBytes = 84;
    private const int StlTriangleBytes = 50;

    /// <summary>
    /// Parse a binary STL file into deduplicated vertices and triangle indices.
    /// The file is read incrementally so a large model never has to be materialised as a single
    /// byte array, and the triangle count is validated against <see cref="MaxTriangles"/> and
    /// the real file length before anything is allocated.
    /// </summary>
    internal static MeshData ParseBinaryStl(string filePath)
    {
        using FileStream file = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = file.Length;
        if (length < StlHeaderBytes)
        {
            throw new InvalidOperationException($"STL file too small ({length} bytes): {filePath}");
        }

        Span<byte> header = stackalloc byte[StlHeaderBytes];
        file.ReadExactly(header);
        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);

        if (triangleCount > MaxTriangles)
        {
            throw new InvalidOperationException(
                $"STL file declares too many triangles ({triangleCount}, limit {MaxTriangles}): {filePath}");
        }

        long expectedSize = StlHeaderBytes + ((long)triangleCount * StlTriangleBytes);
        if (length < expectedSize)
        {
            throw new InvalidOperationException(
                $"STL file size mismatch: expected at least {expectedSize} bytes for {triangleCount} triangles, got {length}");
        }

        var vertexMap = new Dictionary<(float, float, float), int>();
        var vertices = new List<(float X, float Y, float Z)>();
        var triangles = new List<(int V1, int V2, int V3)>((int)triangleCount);

        using BufferedStream buffered = new(file, 1 << 16);
        Span<byte> record = stackalloc byte[StlTriangleBytes];

        for (uint t = 0; t < triangleCount; t++)
        {
            buffered.ReadExactly(record);

            // Layout: 12 bytes normal (skipped), 3 × 12 bytes vertex, 2 bytes attribute count.
            int v1 = DeduplicateVertex(record.Slice(12, 12), vertexMap, vertices);
            int v2 = DeduplicateVertex(record.Slice(24, 12), vertexMap, vertices);
            int v3 = DeduplicateVertex(record.Slice(36, 12), vertexMap, vertices);
            triangles.Add((v1, v2, v3));
        }

        return new MeshData(vertices, triangles);
    }

    /// <summary>Computes the axis-aligned bounds of a parsed mesh.</summary>
    internal static MeshBounds ComputeBounds(MeshData mesh)
    {
        if (mesh.Vertices is null || mesh.Vertices.Count == 0)
        {
            return new MeshBounds(0, 0, 0, 0);
        }

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach ((float x, float y, float z) in mesh.Vertices)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        return new MeshBounds(
            (minX + maxX) / 2,
            (minY + maxY) / 2,
            (minZ + maxZ) / 2,
            (maxZ - minZ) / 2);
    }

    /// <summary>
    /// Build the 3MF <c>transform</c> attribute that places one model exactly where the
    /// workspace shows it.
    /// <para>
    /// The workspace recentres each mesh on its bounding box, rotates and scales it about that
    /// centre, then positions that centre relative to the <em>bed centre</em>, with
    /// <c>position.z == 0</c> meaning "sitting on the bed". OrcaSlicer instead places 3MF build
    /// items in bed coordinates, so the emitted matrix is
    /// <c>Translate(-meshCentre) · Scale · Rotate · Translate(bedCentre + position)</c>.
    /// </para>
    /// <para>
    /// This is the only mechanism available: OrcaSlicer 2.4.2 compiles its <c>--center</c> and
    /// <c>--align-xy</c> CLI options out of <c>CLITransformConfigDef</c>, so there is no
    /// command-line flag that can place a model at an absolute bed coordinate (issue #1794).
    /// </para>
    /// </summary>
    internal static string BuildItemTransform(
        string? transformJson,
        MeshBounds bounds,
        (double X, double Y) bedCenter)
    {
        double[] rot = [0, 0, 0];
        double[] scale = [1, 1, 1];
        double[] pos = [0, 0, 0];

        if (!string.IsNullOrWhiteSpace(transformJson))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(transformJson);
                JsonElement root = doc.RootElement;

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
            }
            catch (JsonException)
            {
                // Malformed transform: fall back to an untransformed model centred on the bed.
            }
        }

        double[] linear = ComputeLinear(rot[0], rot[1], rot[2], scale[0], scale[1], scale[2]);

        // Target for the mesh's own bounding-box centre, in OrcaSlicer bed coordinates.
        double targetX = bedCenter.X + pos[0];
        double targetY = bedCenter.Y + pos[1];

        // The viewer keeps `position.z == 0` on the bed by lifting the centred mesh by half its
        // (untransformed) height; OrcaSlicer re-normalises Z via ensure_on_bed regardless.
        double targetZ = pos[2] + bounds.HalfHeight;

        // Row-vector composition: Translate(-c) · L · Translate(target)
        // ⇒ translation row = target − (c · L).
        double cx = bounds.CenterX, cy = bounds.CenterY, cz = bounds.CenterZ;
        double tx = targetX - ((cx * linear[0]) + (cy * linear[3]) + (cz * linear[6]));
        double ty = targetY - ((cx * linear[1]) + (cy * linear[4]) + (cz * linear[7]));
        double tz = targetZ - ((cx * linear[2]) + (cy * linear[5]) + (cz * linear[8]));

        return FormatMatrix(linear, tx, ty, tz);
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
        double tx, double ty, double tz) =>
        FormatMatrix(ComputeLinear(rx, ry, rz, sx, sy, sz), tx, ty, tz);

    /// <summary>
    /// Upper-left 3×3 of the row-vector transform: Scale × Rotation.
    /// <para>
    /// The rotation must match the workspace viewer, which is the ground truth for what the user
    /// placed. A three.js <c>&lt;group rotation={[rx,ry,rz]}&gt;</c> uses the default Euler order
    /// <c>'XYZ'</c> — column-vector <c>R = Rx·Ry·Rz</c>. Transposed into 3MF's row-vector
    /// convention that is <c>Rz·Ry·Rx</c>, which is NOT the same as appending X, then Y, then Z
    /// here: the two conventions agree only when at most one component is non-zero. Getting it
    /// backwards therefore looks fine in every single-axis test while silently mis-orienting
    /// every auto-oriented model, since <c>autoOrient.ts</c> derives its Euler from a quaternion
    /// and normally yields two or three non-zero components.
    /// </para>
    /// <para>
    /// Oracle (three.js, order 'XYZ', rotation = [π/2, 0, π/2]):
    /// (1,0,0) → (0,0,1), (0,1,0) → (-1,0,0), (0,0,1) → (0,-1,0).
    /// </para>
    /// <para>Scale is applied before rotation, matching three.js' T·R·S group composition.</para>
    /// </summary>
    private static double[] ComputeLinear(double rx, double ry, double rz, double sx, double sy, double sz)
    {
        double cosX = Math.Cos(rx), sinX = Math.Sin(rx);
        double cosY = Math.Cos(ry), sinY = Math.Sin(ry);
        double cosZ = Math.Cos(rz), sinZ = Math.Sin(rz);

        return
        [
            sx * cosY * cosZ,
            sx * ((cosX * sinZ) + (sinX * cosZ * sinY)),
            sx * ((sinX * sinZ) - (cosX * cosZ * sinY)),

            sy * -cosY * sinZ,
            sy * ((cosX * cosZ) - (sinX * sinZ * sinY)),
            sy * ((sinX * cosZ) + (cosX * sinZ * sinY)),

            sz * sinY,
            sz * -sinX * cosY,
            sz * cosX * cosY,
        ];
    }

    private static string FormatMatrix(double[] m, double tx, double ty, double tz) =>
        string.Create(
            Inv,
            $"{C(m[0]):G9} {C(m[1]):G9} {C(m[2]):G9} {C(m[3]):G9} {C(m[4]):G9} {C(m[5]):G9} {C(m[6]):G9} {C(m[7]):G9} {C(m[8]):G9} {C(tx):G9} {C(ty):G9} {C(tz):G9}");

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

    private static int DeduplicateVertex(
        ReadOnlySpan<byte> vertexBytes,
        Dictionary<(float X, float Y, float Z), int> vertexMap,
        List<(float X, float Y, float Z)> vertices)
    {
        float x = BinaryPrimitives.ReadSingleLittleEndian(vertexBytes);
        float y = BinaryPrimitives.ReadSingleLittleEndian(vertexBytes[4..]);
        float z = BinaryPrimitives.ReadSingleLittleEndian(vertexBytes[8..]);

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
