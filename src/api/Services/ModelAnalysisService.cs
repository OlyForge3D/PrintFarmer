using System.Globalization;
using System.Text;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services;

/// <summary>
/// Best-effort model analysis implementation. Currently supports basic analysis for STL files
/// (ASCII and binary) to extract triangle count and bounding-box based dimensions. Returns null
/// for unsupported formats.
/// </summary>
public class ModelAnalysisService : IModelAnalysisService
{
    public async Task<ModelAnalysisResult?> AnalyzeModelAsync(string filePath, string extension, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(filePath) ?? string.Empty;
        }
        // Normalize extension to lower-case once for comparison convenience
        // Keep original extension casing and use explicit OrdinalIgnoreCase comparisons where needed
        // extension = extension.ToLowerInvariant();

        if (extension == ".stl")
        {
            return await AnalyzeStlAsync(filePath, cancellationToken);
        }

        // Currently only STL analysis is implemented; unsupported formats return null
        return null;
    }

    private static async Task<ModelAnalysisResult?> AnalyzeStlAsync(string filePath, CancellationToken cancellationToken)
    {
        // Attempt to detect ASCII vs binary STL
        using FileStream fs = File.OpenRead(filePath);
        if (fs.Length < 84)
        {
            return null; // too small to parse
        }

        byte[] header = new byte[80];
        int bytesRead = await fs.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        if (bytesRead < header.Length)
        {
            return null;
        }

        // Peek triangle count for binary (next 4 bytes)
        byte[] countBytes = new byte[4];
        int countRead = await fs.ReadAsync(countBytes.AsMemory(0, 4), cancellationToken);
        if (countRead < 4)
        {
            return null;
        }

        uint triangleCount = BitConverter.ToUInt32(countBytes, 0);

        // Simple heuristic: if header starts with "solid" then treat as ASCII for small files
        string headerString = Encoding.ASCII.GetString(header).Trim();
        _ = fs.Seek(0, SeekOrigin.Begin);
        if (headerString.StartsWith("solid", StringComparison.OrdinalIgnoreCase) && fs.Length < 10_000_000) // small ASCII models are often ASCII
        {
            // ASCII parser: scan for vertex lines and compute bounding box
            using StreamReader sr = new(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            int vertexCount = 0;
            string? line;
            while ((line = await sr.ReadLineAsync(cancellationToken)) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string trimmed = line.Trim();
                if (trimmed.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 &&
                        double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double vx) &&
                        double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double vy) &&
                        double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double vz))
                    {
                        vertexCount++;
                        if (vx < minX)
                        { minX = vx; }
                        if (vy < minY)
                        { minY = vy; }
                        if (vz < minZ)
                        { minZ = vz; }
                        if (vx > maxX)
                        { maxX = vx; }
                        if (vy > maxY)
                        { maxY = vy; }
                        if (vz > maxZ)
                        { maxZ = vz; }
                    }
                }
            }

            if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ))
            {
                return new ModelAnalysisResult(null, null, null, vertexCount / 3, null);
            }

            double dimX = maxX - minX;
            double dimY = maxY - minY;
            double dimZ = maxZ - minZ;
            // Volume estimation is complex; leave null for now
            return new ModelAnalysisResult(dimX, dimY, dimZ, vertexCount / 3, null);
        }
        else
        {
            // Binary STL: triangleCount available, read bounding box by scanning triangles
            try
            {
                _ = fs.Seek(84, SeekOrigin.Begin);
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
                byte[] buffer = new byte[50];
                uint actualTriangles = 0;
                for (uint i = 0; i < triangleCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read < buffer.Length)
                    {
                        break;
                    }
                    // 3 floats for normal (ignored) + 9 floats for vertices = 12 floats (4 bytes each)
                    // Vertex data starts at offset 12
                    for (int v = 0; v < 3; v++)
                    {
                        int baseIndex = 12 + v * 12; // normal(12) + v*(3*4)
                        float vx = BitConverter.ToSingle(buffer, baseIndex);
                        float vy = BitConverter.ToSingle(buffer, baseIndex + 4);
                        float vz = BitConverter.ToSingle(buffer, baseIndex + 8);
                        if (vx < minX)
                        { minX = vx; }
                        if (vy < minY)
                        { minY = vy; }
                        if (vz < minZ)
                        { minZ = vz; }
                        if (vx > maxX)
                        { maxX = vx; }
                        if (vy > maxY)
                        { maxY = vy; }
                        if (vz > maxZ)
                        { maxZ = vz; }
                    }
                    actualTriangles++;
                }

                if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ))
                {
                    return new ModelAnalysisResult(null, null, null, (int)triangleCount, null);
                }

                double dimX = maxX - minX;
                double dimY = maxY - minY;
                double dimZ = maxZ - minZ;

                return new ModelAnalysisResult(dimX, dimY, dimZ, (int)triangleCount, null);
            }
            catch
            {
                return new ModelAnalysisResult(null, null, null, (int)triangleCount, null);
            }
        }
    }
}
