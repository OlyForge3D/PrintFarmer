using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Farm.Infrastructure.Services.Models;

/// <summary>
/// Best-effort model analysis implementation. Supports STL (ASCII and binary) and 3MF to extract
/// triangle count and bounding-box based dimensions. Returns null for unsupported formats.
/// </summary>
/// <remarks>
/// This is deliberately best-effort metadata extraction, not a slicing pre-flight gate (see
/// issue #1814 / #1811): callers should never treat <see cref="ModelAnalysisResult.IsValid"/> or
/// its dimensions as a printability verdict.
/// </remarks>
public class ModelAnalysisService : IModelAnalysisService
{
    /// <summary>Maximum accepted archive entry count for a 3MF package.</summary>
    private const int MaxArchiveEntries = 1_000;

    /// <summary>Maximum accepted total uncompressed 3MF archive size, in bytes.</summary>
    private const long MaxArchiveUncompressedBytes = 256L * 1024 * 1024;

    /// <summary>Maximum accepted archive compression ratio before the input is treated as a bomb.</summary>
    private const long MaxArchiveCompressionRatio = 200;

    /// <summary>Maximum accepted XML nesting depth inside a 3MF model part.</summary>
    private const int MaxXmlDepth = 64;

    private const string ModelPartSuffix = "3dmodel.model";

    public async Task<ModelAnalysisResult?> AnalyzeModelAsync(string filePath, string extension, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(filePath) ?? string.Empty;
        }

        // Extensions are attacker/user supplied (original upload filename) and commonly vary in
        // case (e.g. "Model.STL", "part.3MF"), so comparisons must be case-insensitive.
        extension = extension.ToLowerInvariant();

        if (extension == ".stl")
        {
            return await AnalyzeStlAsync(filePath, cancellationToken);
        }

        if (extension == ".3mf")
        {
            return await AnalyzeThreeMfAsync(filePath, cancellationToken);
        }

        // Unsupported formats (OBJ, PLY, STEP, ...) return null: unanalyzed, not invalid.
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

        // Small ASCII models (under 10MB) are often ASCII STL format
        if (headerString.StartsWith("solid", StringComparison.OrdinalIgnoreCase) && fs.Length < 10_000_000)
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
                        {
                            minX = vx;
                        }

                        if (vy < minY)
                        {
                            minY = vy;
                        }

                        if (vz < minZ)
                        {
                            minZ = vz;
                        }

                        if (vx > maxX)
                        {
                            maxX = vx;
                        }

                        if (vy > maxY)
                        {
                            maxY = vy;
                        }

                        if (vz > maxZ)
                        {
                            maxZ = vz;
                        }
                    }
                }
            }

            if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ) || vertexCount == 0)
            {
                return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["No triangles found in mesh"]);
            }

            double dimX = maxX - minX;
            double dimY = maxY - minY;
            double dimZ = maxZ - minZ;

            // Volume estimation is complex; leave null for now
            return new ModelAnalysisResult(dimX, dimY, dimZ, vertexCount / 3);
        }
        else
        {
            // Binary STL: triangleCount available, read bounding box by scanning triangles
            uint actualTriangles = 0;
            try
            {
                _ = fs.Seek(84, SeekOrigin.Begin);
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
                byte[] buffer = new byte[50];
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
                        int baseIndex = 12 + (v * 12); // normal(12) + v*(3*4)
                        float vx = BitConverter.ToSingle(buffer, baseIndex);
                        float vy = BitConverter.ToSingle(buffer, baseIndex + 4);
                        float vz = BitConverter.ToSingle(buffer, baseIndex + 8);
                        if (vx < minX)
                        {
                            minX = vx;
                        }

                        if (vy < minY)
                        {
                            minY = vy;
                        }

                        if (vz < minZ)
                        {
                            minZ = vz;
                        }

                        if (vx > maxX)
                        {
                            maxX = vx;
                        }

                        if (vy > maxY)
                        {
                            maxY = vy;
                        }

                        if (vz > maxZ)
                        {
                            maxZ = vz;
                        }
                    }

                    actualTriangles++;
                }

                if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ) || actualTriangles == 0)
                {
                    return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["No triangles found in mesh"]);
                }

                double dimX = maxX - minX;
                double dimY = maxY - minY;
                double dimZ = maxZ - minZ;

                bool truncated = actualTriangles != triangleCount;
                string[]? truncationErrors = truncated
                    ? [$"Declared {triangleCount} triangles but only {actualTriangles} could be read; file may be truncated"]
                    : null;
                return new ModelAnalysisResult(dimX, dimY, dimZ, (int)actualTriangles, IsValid: !truncated, ValidationErrors: truncationErrors);
            }
            catch
            {
                return new ModelAnalysisResult(null, null, null, (int)actualTriangles, IsValid: false, ValidationErrors: ["Failed to read binary STL triangle data"]);
            }
        }
    }

    /// <summary>
    /// Analyzes a 3MF package to extract triangle count and bounding-box dimensions.
    /// </summary>
    /// <remarks>
    /// A 3MF is a ZIP archive containing a "3D/3dmodel.model" XML part describing one or more
    /// mesh objects. Per-object build-item transforms are intentionally not applied here — this
    /// mirrors the object's own local coordinates, which is sufficient for best-effort metadata
    /// and avoids re-implementing full 3MF assembly resolution. Basic archive-bomb and unsafe-XML
    /// protections are applied since the file is attacker/user supplied.
    /// </remarks>
    private static async Task<ModelAnalysisResult?> AnalyzeThreeMfAsync(string filePath, CancellationToken cancellationToken)
    {
        using FileStream fs = File.OpenRead(filePath);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model package is not a readable archive"]);
        }

        using (archive)
        {
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model package declares too many archive entries"]);
            }

            long totalUncompressed = 0;
            long totalCompressed = 0;
            ZipArchiveEntry? modelEntry = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                totalUncompressed += entry.Length;
                totalCompressed += entry.CompressedLength;
                if (totalUncompressed > MaxArchiveUncompressedBytes)
                {
                    return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model package expands beyond the accepted budget"]);
                }

                if (modelEntry is null && entry.FullName.EndsWith(ModelPartSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    modelEntry = entry;
                }
            }

            if (totalCompressed > 0 && totalUncompressed / Math.Max(totalCompressed, 1) > MaxArchiveCompressionRatio)
            {
                return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model package compression ratio indicates a decompression bomb"]);
            }

            if (modelEntry is null)
            {
                return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model package does not contain a 3D model part"]);
            }

            return await ReadThreeMfModelPartAsync(modelEntry, cancellationToken);
        }
    }

    private static async Task<ModelAnalysisResult?> ReadThreeMfModelPartAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
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
            Async = true,
        };

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        int triangleCount = 0;
        bool sawMesh = false;

        try
        {
            await using Stream partStream = await entry.OpenAsync(cancellationToken);
            using XmlReader reader = XmlReader.Create(partStream, settings);
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.Depth > MaxXmlDepth)
                {
                    return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model part XML is nested beyond the accepted depth"]);
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "mesh":
                        sawMesh = true;
                        break;
                    case "vertex":
                        if (TryReadCoordinate(reader, "x", out double vx) &&
                            TryReadCoordinate(reader, "y", out double vy) &&
                            TryReadCoordinate(reader, "z", out double vz))
                        {
                            if (vx < minX)
                            {
                                minX = vx;
                            }

                            if (vy < minY)
                            {
                                minY = vy;
                            }

                            if (vz < minZ)
                            {
                                minZ = vz;
                            }

                            if (vx > maxX)
                            {
                                maxX = vx;
                            }

                            if (vy > maxY)
                            {
                                maxY = vy;
                            }

                            if (vz > maxZ)
                            {
                                maxZ = vz;
                            }
                        }

                        break;
                    case "triangle":
                        triangleCount++;
                        break;
                    default:
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model part XML is malformed or unsafe"]);
        }
        catch (InvalidDataException)
        {
            return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model package could not be decompressed"]);
        }

        if (!sawMesh || triangleCount == 0 || double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ))
        {
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["Model part declares no printable mesh"]);
        }

        return new ModelAnalysisResult(maxX - minX, maxY - minY, maxZ - minZ, triangleCount);
    }

    private static bool TryReadCoordinate(XmlReader reader, string attributeName, out double value)
    {
        value = 0d;
        string? raw = reader.GetAttribute(attributeName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            double.IsNaN(parsed) ||
            double.IsInfinity(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
