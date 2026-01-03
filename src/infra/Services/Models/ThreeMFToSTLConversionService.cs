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

            // Find the main 3D model file (usually 3D/3dmodel.model)
            var modelEntry = zipArchive.Entries.FirstOrDefault(e => 
                e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));

            if (modelEntry == null)
            {
                _logger.LogWarning("No .model file found in 3MF archive");
                return null;
            }

            // Parse the XML model file
            using var modelStream = modelEntry.Open();
            var xmlDoc = new XmlDocument();
            await Task.Run(() => xmlDoc.Load(modelStream), cancellationToken);

            // Extract mesh data and convert to STL
            var stlBytes = await ConvertMeshToSTLAsync(xmlDoc, cancellationToken);
            
            _logger.LogInformation($"3MF to STL conversion completed, output size: {stlBytes?.Length ?? 0} bytes");
            return stlBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to convert 3MF to STL: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> ConvertMeshToSTLAsync(XmlDocument xmlDoc, CancellationToken cancellationToken)
    {
        try
        {
            // Parse 3MF XML and extract vertices and triangles
            var namespaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
            namespaceManager.AddNamespace("model", "http://schemas.microsoft.com/3dmanufacturing/core/2015/02");

            // Get vertices
            var verticesNode = xmlDoc.SelectSingleNode("//model:vertices", namespaceManager);
            if (verticesNode == null)
            {
                _logger.LogWarning("No vertices found in 3MF model");
                return null;
            }

            var vertices = new List<(float x, float y, float z)>();
            foreach (XmlNode vertexNode in verticesNode.SelectNodes("model:vertex", namespaceManager)!)
            {
                var x = float.Parse(vertexNode.Attributes!["x"]!.Value);
                var y = float.Parse(vertexNode.Attributes!["y"]!.Value);
                var z = float.Parse(vertexNode.Attributes!["z"]!.Value);
                vertices.Add((x, y, z));
            }

            // Get triangles
            var trianglesNode = xmlDoc.SelectSingleNode("//model:triangles", namespaceManager);
            if (trianglesNode == null)
            {
                _logger.LogWarning("No triangles found in 3MF model");
                return null;
            }

            var triangles = new List<(int v1, int v2, int v3)>();
            foreach (XmlNode triangleNode in trianglesNode.SelectNodes("model:triangle", namespaceManager)!)
            {
                var v1 = int.Parse(triangleNode.Attributes!["v1"]!.Value);
                var v2 = int.Parse(triangleNode.Attributes!["v2"]!.Value);
                var v3 = int.Parse(triangleNode.Attributes!["v3"]!.Value);
                triangles.Add((v1, v2, v3));
            }

            // Convert to binary STL format
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
}
