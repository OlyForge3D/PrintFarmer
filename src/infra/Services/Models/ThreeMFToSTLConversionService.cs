using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Farm.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Models;

/// <summary>
/// Implementation of 3MF to STL conversion service
/// Properly handles 3MF assemblies by:
/// 1. Parsing component references and transform matrices
/// 2. Loading all referenced object meshes
/// 3. Applying transformations to vertices
/// 4. Merging into a single coherent mesh
/// </summary>
public class ThreeMfToStlConversionService(ILogger<ThreeMfToStlConversionService> logger) : I3MfToStlConversionService
{
    private readonly ILogger<ThreeMfToStlConversionService> _logger = logger;

    /// <inheritdoc/>
    public async Task<byte[]?> ConvertToSTLAsync(byte[] threeMfBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting 3MF to STL conversion");

            using var memoryStream = new MemoryStream(threeMfBytes);
            using var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

            // Find the main 3D/3dmodel.model file
            ZipArchiveEntry? mainModelEntry = zipArchive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase));

            if (mainModelEntry == null)
            {
                _logger.LogWarning("No 3D/3dmodel.model file found in 3MF archive");
                return null;
            }

            // Parse the main model file
            XmlDocument mainModelXml = new XmlDocument();
            using (Stream modelStream = await mainModelEntry.OpenAsync(cancellationToken))
            {
                await Task.Run(() => mainModelXml.Load(modelStream), cancellationToken);
            }

            // Collect component data with bounding boxes for grid layout
            List<ComponentData> components = [];

            // Parse component references and convert them
            var namespaceManager = new XmlNamespaceManager(mainModelXml.NameTable);
            namespaceManager.AddNamespace("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");
            namespaceManager.AddNamespace("p", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06");

            XmlNodeList? componentNodes = mainModelXml.SelectNodes("//model:component", namespaceManager);
            _logger.LogInformation("Found {ComponentNodesCount} components in assembly", componentNodes?.Count ?? 0);

            if (componentNodes?.Count == 0)
            {
                _logger.LogWarning("No components found in main model, attempting to extract direct mesh");
                byte[]? stlBytes = await ConvertMeshToSTLAsync(mainModelXml, cancellationToken);
                if (stlBytes != null)
                {
                    _logger.LogInformation("Successfully converted main model directly");
                    return stlBytes;
                }
            }

            int componentIndex = 0;
            foreach (XmlNode componentNode in componentNodes!)
            {
                XmlAttribute? pathAttr = componentNode.Attributes?["p:path"] ?? componentNode.Attributes?["path"];
                XmlAttribute? transformAttr = componentNode.Attributes?["transform"];

                if (pathAttr == null)
                {
                    _logger.LogWarning("Component {ComponentIndex} missing path attribute", componentIndex);
                    componentIndex++;
                    continue;
                }

                string refPath = pathAttr.Value.TrimStart('/');
                _logger.LogInformation("Processing component {ComponentIndex}: {RefPath}", componentIndex, refPath);

                // Find the referenced object file in the archive
                ZipArchiveEntry? refEntry = zipArchive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals(refPath, StringComparison.OrdinalIgnoreCase));

                if (refEntry == null)
                {
                    _logger.LogWarning("Component reference not found: {RefPath}", refPath);
                    componentIndex++;
                    continue;
                }

                // Parse the referenced object file
                XmlDocument refXmlDoc = new XmlDocument();
                using (Stream refStream = await refEntry.OpenAsync(cancellationToken))
                {
                    await Task.Run(() => refXmlDoc.Load(refStream), cancellationToken);
                }

                // Extract vertices and triangles from this object
                (List<(float X, float Y, float Z)>? vertices, List<(int V1, int V2, int V3)>? triangles) = ExtractMeshData(refXmlDoc);

                if (vertices.Count == 0)
                {
                    _logger.LogWarning("Component {ComponentIndex} has no vertices", componentIndex);
                    componentIndex++;
                    continue;
                }

                _logger.LogInformation("Component {ComponentIndex}: {VerticesCount} vertices, {TrianglesCount} triangles", componentIndex, vertices.Count, triangles.Count);

                // Parse and apply original transform matrix if present
                List<(float X, float Y, float Z)> transformedVertices = ApplyTransform(vertices, transformAttr?.Value);

                // Calculate bounding box for this component
                CalculateBoundingBox(transformedVertices, out float minX, out float maxX, out float minY, out float maxY, out float minZ, out float maxZ);

                components.Add(new ComponentData
                {
                    Index = componentIndex,
                    Vertices = transformedVertices,
                    Triangles = triangles,
                    MinX = minX,
                    MaxX = maxX,
                    MinY = minY,
                    MaxY = maxY,
                    MinZ = minZ,
                    MaxZ = maxZ
                });

                componentIndex++;
            }

            if (components.Count == 0)
            {
                _logger.LogError("No vertices found in any component of the 3MF assembly");
                return null;
            }

            // Calculate grid layout positions (on XY plane, with padding)
            const float padding = 5.0f; // 5mm padding between objects
            ApplyGridLayout(components, padding);

            // Merge all positioned components into a single mesh
            List<(float X, float Y, float Z)> allVertices = [];
            List<(int V1, int V2, int V3)> allTriangles = [];

            foreach (ComponentData component in components)
            {
                // Apply grid position offset to vertices
                var positionedVertices = component.Vertices.Select(v =>
                    (v.X + component.GridOffsetX, v.Y + component.GridOffsetY, v.Z))
                .ToList();

                int vertexOffset = allVertices.Count;
                allVertices.AddRange(positionedVertices);
                allTriangles.AddRange(component.Triangles.Select(t =>
                    (t.V1 + vertexOffset, t.V2 + vertexOffset, t.V3 + vertexOffset)));
            }

            _logger.LogInformation("Combined mesh (grid layout): {AllVerticesCount} total vertices, {AllTrianglesCount} total triangles", allVertices.Count, allTriangles.Count);

            // Generate STL from the combined mesh
            byte[] stlResult = GenerateBinarySTL(allVertices, allTriangles);
            _logger.LogInformation("3MF to STL conversion completed, output size: {StlResultLength} bytes", stlResult.Length);
            return stlResult;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to convert 3MF to STL: {Name}: {Message}", ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extracts vertices and triangles from a 3MF model XML document
    /// </summary>
    private (List<(float X, float Y, float Z)> Vertices, List<(int V1, int V2, int V3)> Triangles) ExtractMeshData(XmlDocument xmlDoc)
    {
        List<(float X, float Y, float Z)> vertices = [];
        List<(int V1, int V2, int V3)> triangles = [];

        var namespaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
        namespaceManager.AddNamespace("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");

        // Extract vertices
        XmlNode? verticesNode = xmlDoc.SelectSingleNode("//model:vertices", namespaceManager);
        if (verticesNode != null)
        {
            foreach (XmlNode vertexNode in verticesNode.SelectNodes("model:vertex", namespaceManager)!)
            {
                try
                {
                    float x = float.Parse(vertexNode.Attributes!["x"]!.Value);
                    float y = float.Parse(vertexNode.Attributes!["y"]!.Value);
                    float z = float.Parse(vertexNode.Attributes!["z"]!.Value);
                    vertices.Add((x, y, z));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to parse vertex: {Message}", ex.Message);
                }
            }
        }

        // Extract triangles
        XmlNode? trianglesNode = xmlDoc.SelectSingleNode("//model:triangles", namespaceManager);
        if (trianglesNode != null)
        {
            foreach (XmlNode triangleNode in trianglesNode.SelectNodes("model:triangle", namespaceManager)!)
            {
                try
                {
                    int v1 = int.Parse(triangleNode.Attributes!["v1"]!.Value);
                    int v2 = int.Parse(triangleNode.Attributes!["v2"]!.Value);
                    int v3 = int.Parse(triangleNode.Attributes!["v3"]!.Value);
                    triangles.Add((v1, v2, v3));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to parse triangle: {Message}", ex.Message);
                }
            }
        }

        return (vertices, triangles);
    }

    /// <summary>
    /// Applies a 4x3 transformation matrix to vertices
    /// Transform format: "m00 m01 m02 m10 m11 m12 m20 m21 m22 tx ty tz"
    /// Or 16 floats for a full 4x4 matrix (we use top-left 3x3 and rightmost column)
    /// </summary>
    private List<(float X, float Y, float Z)> ApplyTransform(List<(float X, float Y, float Z)> vertices, string? transformStr)
    {
        if (string.IsNullOrWhiteSpace(transformStr))
        {
            // No transform, return as-is
            return vertices;
        }

        try
        {
            string[] parts = transformStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 12)
            {
                _logger.LogWarning("Invalid transform matrix: expected at least 12 values, got {PartsLength}", parts.Length);
                return vertices;
            }

            // Parse 4x3 or 4x4 matrix
            float[] matrix = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out float val))
                {
                    _logger.LogWarning("Failed to parse transform matrix value at index {I}: {Value1}", i, parts[i]);
                    return vertices;
                }

                matrix[i] = val;
            }

            // Apply transformation to each vertex
            List<(float X, float Y, float Z)> transformed = [];
            foreach ((float x, float y, float z) in vertices)
            {
                // 4x3 matrix multiplication: [x' y' z'] = [x y z 1] * M^T
                // But stored as row-major: m0 m1 m2 m3, m4 m5 m6 m7, m8 m9 m10 m11
                // So: x' = m0*x + m4*y + m8*z + m12
                float x_new = (matrix[0] * x) + (matrix[4] * y) + (matrix[8] * z) + matrix[12];
                float y_new = (matrix[1] * x) + (matrix[5] * y) + (matrix[9] * z) + matrix[13];
                float z_new = (matrix[2] * x) + (matrix[6] * y) + (matrix[10] * z) + matrix[14];

                transformed.Add((x_new, y_new, z_new));
            }

            return transformed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to apply transform: {Message}", ex.Message);
            return vertices;
        }
    }

    /// <summary>
    /// Converts mesh data directly from XML (for single-part 3MF files with no assembly)
    /// </summary>
    private async Task<byte[]?> ConvertMeshToSTLAsync(XmlDocument xmlDoc, CancellationToken cancellationToken)
    {
        try
        {
            (List<(float X, float Y, float Z)>? vertices, List<(int V1, int V2, int V3)>? triangles) = ExtractMeshData(xmlDoc);

            if (vertices.Count == 0)
            {
                _logger.LogWarning("No vertices found in model");
                return null;
            }

            _logger.LogInformation("Direct mesh: {VerticesCount} vertices, {TrianglesCount} triangles", vertices.Count, triangles.Count);
            return await Task.Run(() => GenerateBinarySTL(vertices, triangles), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to parse 3MF mesh data: {Message}", ex.Message);
            return null;
        }
    }

    private static byte[] GenerateBinarySTL(List<(float X, float Y, float Z)> vertices, List<(int V1, int V2, int V3)> triangles)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new BinaryWriter(memoryStream);

        // STL header (80 bytes) - can be anything
        byte[] header = new byte[80];
        byte[] headerText = "Converted from 3MF"u8.ToArray();
        Array.Copy(headerText, header, Math.Min(headerText.Length, 80));
        writer.Write(header);

        // Number of triangles (4 bytes, little-endian uint32)
        writer.Write((uint)triangles.Count);

        // Write each triangle
        foreach ((int V1, int V2, int V3) triangle in triangles)
        {
            (float X, float Y, float Z) v1 = vertices[triangle.V1];
            (float X, float Y, float Z) v2 = vertices[triangle.V2];
            (float X, float Y, float Z) v3 = vertices[triangle.V3];

            // Calculate normal vector (cross product)
            (float, float, float) edge1 = (v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z);
            (float, float, float) edge2 = (v3.X - v1.X, v3.Y - v1.Y, v3.Z - v1.Z);

            (float, float, float) normal = (
                (edge1.Item2 * edge2.Item3) - (edge1.Item3 * edge2.Item2),
                (edge1.Item3 * edge2.Item1) - (edge1.Item1 * edge2.Item3),
                (edge1.Item1 * edge2.Item2) - (edge1.Item2 * edge2.Item1));

            // Normalize the normal vector
            float length = (float)Math.Sqrt((normal.Item1 * normal.Item1) + (normal.Item2 * normal.Item2) + (normal.Item3 * normal.Item3));
            if (length > 0)
            {
                normal = (normal.Item1 / length, normal.Item2 / length, normal.Item3 / length);
            }

            // Write normal vector (12 bytes)
            writer.Write(normal.Item1);
            writer.Write(normal.Item2);
            writer.Write(normal.Item3);

            // Write vertices (36 bytes total)
            writer.Write(v1.X);
            writer.Write(v1.Y);
            writer.Write(v1.Z);
            writer.Write(v2.X);
            writer.Write(v2.Y);
            writer.Write(v2.Z);
            writer.Write(v3.X);
            writer.Write(v3.Y);
            writer.Write(v3.Z);

            // Attribute byte count (2 bytes) - usually 0
            writer.Write((ushort)0);
        }

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Helper struct to track component data and grid layout information
    /// </summary>
    private class ComponentData
    {
        public int Index { get; set; }

        public List<(float X, float Y, float Z)> Vertices { get; set; } = new();

        public List<(int V1, int V2, int V3)> Triangles { get; set; } = new();

        // Bounding box in original coordinates
        public float MinX { get; set; }

        public float MaxX { get; set; }

        public float MinY { get; set; }

        public float MaxY { get; set; }

        public float MinZ { get; set; }

        public float MaxZ { get; set; }

        // Grid layout offsets to position on XY plane
        public float GridOffsetX { get; set; }

        public float GridOffsetY { get; set; }

        public float Width => MaxX - MinX;

        public float Length => MaxY - MinY;
    }

    /// <summary>
    /// Calculates the bounding box of a set of vertices
    /// </summary>
    private void CalculateBoundingBox(
        List<(float X, float Y, float Z)> vertices,
        out float minX, out float maxX,
        out float minY, out float maxY,
        out float minZ, out float maxZ)
    {
        minX = minY = minZ = float.MaxValue;
        maxX = maxY = maxZ = float.MinValue;

        foreach ((float x, float y, float z) in vertices)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
        }
    }

    /// <summary>
    /// Applies a grid layout to position components on the build plate (XY plane)
    /// Components are arranged in rows and columns with padding between them
    /// </summary>
    private void ApplyGridLayout(List<ComponentData> components, float padding)
    {
        if (components.Count == 0)
        {
            return;
        }

        // Sort by size (largest first) for better packing
        var sortedComponents = components.OrderByDescending(c => c.Width * c.Length).ToList();

        // Determine grid dimensions (square-ish grid)
        int gridCols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(sortedComponents.Count)));
        int gridRows = (sortedComponents.Count + gridCols - 1) / gridCols;

        _logger.LogInformation("Grid layout: {GridRows} rows × {GridCols} columns", gridRows, gridCols);

        // Calculate column widths and row heights for better packing
        float[] colWidths = new float[gridCols];
        float[] rowHeights = new float[gridRows];

        // First pass: determine required space for each cell
        for (int i = 0; i < sortedComponents.Count; i++)
        {
            int row = i / gridCols;
            int col = i % gridCols;

            colWidths[col] = Math.Max(colWidths[col], sortedComponents[i].Width);
            rowHeights[row] = Math.Max(rowHeights[row], sortedComponents[i].Length);
        }

        // Calculate positions based on accumulated widths and heights
        float[] colPositions = new float[gridCols];
        float[] rowPositions = new float[gridRows];

        float accum = 0;
        for (int col = 0; col < gridCols; col++)
        {
            colPositions[col] = accum;
            accum += colWidths[col] + padding;
        }

        accum = 0;
        for (int row = 0; row < gridRows; row++)
        {
            rowPositions[row] = accum;
            accum += rowHeights[row] + padding;
        }

        // Apply grid positions to components
        for (int i = 0; i < sortedComponents.Count; i++)
        {
            int row = i / gridCols;
            int col = i % gridCols;

            // Position the component at grid cell position, offset to align with its bounding box
            sortedComponents[i].GridOffsetX = colPositions[col] - sortedComponents[i].MinX;
            sortedComponents[i].GridOffsetY = rowPositions[row] - sortedComponents[i].MinY;

            _logger.LogInformation("Component {I}: grid position ({Row}, {Col}) → offset ({Value3:F1}, {Value4:F1})", i, row, col, sortedComponents[i].GridOffsetX, sortedComponents[i].GridOffsetY);
        }

        // Re-sync back to original components list
        for (int i = 0; i < components.Count; i++)
        {
            ComponentData? sorted = sortedComponents.FirstOrDefault(c => c.Index == components[i].Index);
            if (sorted != null)
            {
                components[i].GridOffsetX = sorted.GridOffsetX;
                components[i].GridOffsetY = sorted.GridOffsetY;
            }
        }
    }
}
