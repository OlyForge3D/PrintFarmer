using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Models;

/// <summary>
/// Service for converting 3MF files to STL format for viewing
/// </summary>
public interface I3MFToSTLConversionService
{
    /// <summary>
    /// Converts a 3MF file to STL format
    /// </summary>
    /// <param name="threeMfBytes">The 3MF file content as bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>STL file content as bytes, or null if conversion failed</returns>
    Task<byte[]?> ConvertToSTLAsync(byte[] threeMfBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of 3MF to STL conversion service
/// Properly handles 3MF assemblies by:
/// 1. Parsing component references and transform matrices
/// 2. Loading all referenced object meshes
/// 3. Applying transformations to vertices
/// 4. Merging into a single coherent mesh
/// </summary>
public class ThreeMFToSTLConversionService : I3MFToSTLConversionService
{
    private readonly IUnifiedLoggingService _logger;

    public ThreeMFToSTLConversionService(IUnifiedLoggingService logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<byte[]?> ConvertToSTLAsync(byte[] threeMfBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting 3MF to STL conversion");

            using var memoryStream = new MemoryStream(threeMfBytes);
            using var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Read);

            // Find the main 3D/3dmodel.model file
            var mainModelEntry = zipArchive.Entries.FirstOrDefault(e => 
                e.FullName.Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase));

            if (mainModelEntry == null)
            {
                _logger.LogWarning("No 3D/3dmodel.model file found in 3MF archive");
                return null;
            }

            // Parse the main model file
            XmlDocument mainModelXml = new XmlDocument();
            using (var modelStream = mainModelEntry.Open())
            {
                await Task.Run(() => mainModelXml.Load(modelStream), cancellationToken);
            }

            // Collect component data with bounding boxes for grid layout
            var components = new List<ComponentData>();

            // Parse component references and convert them
            var namespaceManager = new XmlNamespaceManager(mainModelXml.NameTable);
            namespaceManager.AddNamespace("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");
            namespaceManager.AddNamespace("p", "http://schemas.microsoft.com/3dmanufacturing/production/2015/06");

            var componentNodes = mainModelXml.SelectNodes("//model:component", namespaceManager);
            _logger.LogInformation($"Found {componentNodes?.Count ?? 0} components in assembly");

            if (componentNodes?.Count == 0)
            {
                _logger.LogWarning("No components found in main model, attempting to extract direct mesh");
                var stlBytes = await ConvertMeshToSTLAsync(mainModelXml, cancellationToken);
                if (stlBytes != null)
                {
                    _logger.LogInformation("Successfully converted main model directly");
                    return stlBytes;
                }
            }

            int componentIndex = 0;
            foreach (XmlNode componentNode in componentNodes!)
            {
                var pathAttr = componentNode.Attributes?["p:path"] ?? componentNode.Attributes?["path"];
                var transformAttr = componentNode.Attributes?["transform"];
                
                if (pathAttr == null)
                {
                    _logger.LogWarning($"Component {componentIndex} missing path attribute");
                    componentIndex++;
                    continue;
                }

                var refPath = pathAttr.Value.TrimStart('/');
                _logger.LogInformation($"Processing component {componentIndex}: {refPath}");

                // Find the referenced object file in the archive
                var refEntry = zipArchive.Entries.FirstOrDefault(e => 
                    e.FullName.Equals(refPath, StringComparison.OrdinalIgnoreCase));

                if (refEntry == null)
                {
                    _logger.LogWarning($"Component reference not found: {refPath}");
                    componentIndex++;
                    continue;
                }

                // Parse the referenced object file
                XmlDocument refXmlDoc = new XmlDocument();
                using (var refStream = refEntry.Open())
                {
                    await Task.Run(() => refXmlDoc.Load(refStream), cancellationToken);
                }

                // Extract vertices and triangles from this object
                (var vertices, var triangles) = ExtractMeshData(refXmlDoc);

                if (vertices.Count == 0)
                {
                    _logger.LogWarning($"Component {componentIndex} has no vertices");
                    componentIndex++;
                    continue;
                }

                _logger.LogInformation($"Component {componentIndex}: {vertices.Count} vertices, {triangles.Count} triangles");

                // Parse and apply original transform matrix if present
                var transformedVertices = ApplyTransform(vertices, transformAttr?.Value);

                // Calculate bounding box for this component
                CalculateBoundingBox(transformedVertices, out var minX, out var maxX, out var minY, out var maxY, out var minZ, out var maxZ);

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
            var allVertices = new List<(float x, float y, float z)>();
            var allTriangles = new List<(int v1, int v2, int v3)>();

            foreach (var component in components)
            {
                // Apply grid position offset to vertices
                var positionedVertices = component.Vertices.Select(v => 
                    (v.x + component.GridOffsetX, v.y + component.GridOffsetY, v.z)
                ).ToList();

                int vertexOffset = allVertices.Count;
                allVertices.AddRange(positionedVertices);
                allTriangles.AddRange(component.Triangles.Select(t => 
                    (t.v1 + vertexOffset, t.v2 + vertexOffset, t.v3 + vertexOffset)
                ));
            }

            _logger.LogInformation($"Combined mesh (grid layout): {allVertices.Count} total vertices, {allTriangles.Count} total triangles");

            // Generate STL from the combined mesh
            var stlResult = GenerateBinarySTL(allVertices, allTriangles);
            _logger.LogInformation($"3MF to STL conversion completed, output size: {stlResult.Length} bytes");
            return stlResult;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to convert 3MF to STL: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts vertices and triangles from a 3MF model XML document
    /// </summary>
    private (List<(float x, float y, float z)> vertices, List<(int v1, int v2, int v3)> triangles) ExtractMeshData(XmlDocument xmlDoc)
    {
        var vertices = new List<(float x, float y, float z)>();
        var triangles = new List<(int v1, int v2, int v3)>();

        var namespaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
        namespaceManager.AddNamespace("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");

        // Extract vertices
        var verticesNode = xmlDoc.SelectSingleNode("//model:vertices", namespaceManager);
        if (verticesNode != null)
        {
            foreach (XmlNode vertexNode in verticesNode.SelectNodes("model:vertex", namespaceManager)!)
            {
                try
                {
                    var x = float.Parse(vertexNode.Attributes!["x"]!.Value);
                    var y = float.Parse(vertexNode.Attributes!["y"]!.Value);
                    var z = float.Parse(vertexNode.Attributes!["z"]!.Value);
                    vertices.Add((x, y, z));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse vertex: {ex.Message}");
                }
            }
        }

        // Extract triangles
        var trianglesNode = xmlDoc.SelectSingleNode("//model:triangles", namespaceManager);
        if (trianglesNode != null)
        {
            foreach (XmlNode triangleNode in trianglesNode.SelectNodes("model:triangle", namespaceManager)!)
            {
                try
                {
                    var v1 = int.Parse(triangleNode.Attributes!["v1"]!.Value);
                    var v2 = int.Parse(triangleNode.Attributes!["v2"]!.Value);
                    var v3 = int.Parse(triangleNode.Attributes!["v3"]!.Value);
                    triangles.Add((v1, v2, v3));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to parse triangle: {ex.Message}");
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
    private List<(float x, float y, float z)> ApplyTransform(List<(float x, float y, float z)> vertices, string? transformStr)
    {
        if (string.IsNullOrWhiteSpace(transformStr))
        {
            // No transform, return as-is
            return vertices;
        }

        try
        {
            var parts = transformStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 12)
            {
                _logger.LogWarning($"Invalid transform matrix: expected at least 12 values, got {parts.Length}");
                return vertices;
            }

            // Parse 4x3 or 4x4 matrix
            var matrix = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out var val))
                {
                    _logger.LogWarning($"Failed to parse transform matrix value at index {i}: {parts[i]}");
                    return vertices;
                }
                matrix[i] = val;
            }

            // Apply transformation to each vertex
            var transformed = new List<(float x, float y, float z)>();
            foreach (var (x, y, z) in vertices)
            {
                // 4x3 matrix multiplication: [x' y' z'] = [x y z 1] * M^T
                // But stored as row-major: m0 m1 m2 m3, m4 m5 m6 m7, m8 m9 m10 m11
                // So: x' = m0*x + m4*y + m8*z + m12
                var x_new = matrix[0] * x + matrix[4] * y + matrix[8] * z + matrix[12];
                var y_new = matrix[1] * x + matrix[5] * y + matrix[9] * z + matrix[13];
                var z_new = matrix[2] * x + matrix[6] * y + matrix[10] * z + matrix[14];

                transformed.Add((x_new, y_new, z_new));
            }

            return transformed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to apply transform: {ex.Message}");
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
            (var vertices, var triangles) = ExtractMeshData(xmlDoc);

            if (vertices.Count == 0)
            {
                _logger.LogWarning("No vertices found in model");
                return null;
            }

            _logger.LogInformation($"Direct mesh: {vertices.Count} vertices, {triangles.Count} triangles");
            return await Task.Run(() => GenerateBinarySTL(vertices, triangles), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to parse 3MF mesh data: {ex.Message}");
            return null;
        }
    }

    private static byte[] GenerateBinarySTL(List<(float x, float y, float z)> vertices, List<(int v1, int v2, int v3)> triangles)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new BinaryWriter(memoryStream);

        // STL header (80 bytes) - can be anything
        var header = new byte[80];
        var headerText = "Converted from 3MF"u8.ToArray();
        Array.Copy(headerText, header, Math.Min(headerText.Length, 80));
        writer.Write(header);

        // Number of triangles (4 bytes, little-endian uint32)
        writer.Write((uint)triangles.Count);

        // Write each triangle
        foreach (var triangle in triangles)
        {
            var v1 = vertices[triangle.v1];
            var v2 = vertices[triangle.v2];
            var v3 = vertices[triangle.v3];

            // Calculate normal vector (cross product)
            var edge1 = (v2.x - v1.x, v2.y - v1.y, v2.z - v1.z);
            var edge2 = (v3.x - v1.x, v3.y - v1.y, v3.z - v1.z);
            
            var normal = (
                edge1.Item2 * edge2.Item3 - edge1.Item3 * edge2.Item2,
                edge1.Item3 * edge2.Item1 - edge1.Item1 * edge2.Item3,
                edge1.Item1 * edge2.Item2 - edge1.Item2 * edge2.Item1
            );

            // Normalize the normal vector
            var length = (float)Math.Sqrt(normal.Item1 * normal.Item1 + normal.Item2 * normal.Item2 + normal.Item3 * normal.Item3);
            if (length > 0)
            {
                normal = (normal.Item1 / length, normal.Item2 / length, normal.Item3 / length);
            }

            // Write normal vector (12 bytes)
            writer.Write(normal.Item1);
            writer.Write(normal.Item2);
            writer.Write(normal.Item3);

            // Write vertices (36 bytes total)
            writer.Write(v1.x);
            writer.Write(v1.y);
            writer.Write(v1.z);
            writer.Write(v2.x);
            writer.Write(v2.y);
            writer.Write(v2.z);
            writer.Write(v3.x);
            writer.Write(v3.y);
            writer.Write(v3.z);

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
        public List<(float x, float y, float z)> Vertices { get; set; } = new();
        public List<(int v1, int v2, int v3)> Triangles { get; set; } = new();
        
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
        List<(float x, float y, float z)> vertices,
        out float minX, out float maxX,
        out float minY, out float maxY,
        out float minZ, out float maxZ)
    {
        minX = minY = minZ = float.MaxValue;
        maxX = maxY = maxZ = float.MinValue;

        foreach (var (x, y, z) in vertices)
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
            return;

        // Sort by size (largest first) for better packing
        var sortedComponents = components.OrderByDescending(c => c.Width * c.Length).ToList();

        // Determine grid dimensions (square-ish grid)
        int gridCols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(sortedComponents.Count)));
        int gridRows = (sortedComponents.Count + gridCols - 1) / gridCols;

        _logger.LogInformation($"Grid layout: {gridRows} rows × {gridCols} columns");

        // Calculate column widths and row heights for better packing
        var colWidths = new float[gridCols];
        var rowHeights = new float[gridRows];

        // First pass: determine required space for each cell
        for (int i = 0; i < sortedComponents.Count; i++)
        {
            int row = i / gridCols;
            int col = i % gridCols;
            
            colWidths[col] = Math.Max(colWidths[col], sortedComponents[i].Width);
            rowHeights[row] = Math.Max(rowHeights[row], sortedComponents[i].Length);
        }

        // Calculate positions based on accumulated widths and heights
        var colPositions = new float[gridCols];
        var rowPositions = new float[gridRows];

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

            _logger.LogInformation($"Component {i}: grid position ({row}, {col}) → offset ({sortedComponents[i].GridOffsetX:F1}, {sortedComponents[i].GridOffsetY:F1})");
        }

        // Re-sync back to original components list
        for (int i = 0; i < components.Count; i++)
        {
            var sorted = sortedComponents.FirstOrDefault(c => c.Index == components[i].Index);
            if (sorted != null)
            {
                components[i].GridOffsetX = sorted.GridOffsetX;
                components[i].GridOffsetY = sorted.GridOffsetY;
            }
        }
    }
}
