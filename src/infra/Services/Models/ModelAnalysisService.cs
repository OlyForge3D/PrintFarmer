using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Farm.Infrastructure.Services.Models;

/// <summary>
/// Best-effort model analysis implementation. Supports STL (ASCII and binary), 3MF, and OBJ to
/// extract triangle/face count and bounding-box based dimensions. Returns null for unsupported
/// formats.
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

    /// <summary>
    /// Maximum accepted STL file size, in bytes. This is a true backstop, not a validity gate:
    /// a rejected file becomes <c>IsValid: false</c>, which the metadata backfill service persists
    /// onto <c>Model3DFile.IsValid</c> — a column several repository queries use to filter model
    /// *visibility* (e.g. <c>ListValidAsync</c>), not just printability. So this ceiling MUST sit
    /// above every size the <c>/api/3d-models/upload</c> endpoint already accepts (500 MB model +
    /// 10 MiB thumbnail + multipart overhead, capped at 512,000,000 bytes total per the #1838
    /// review), or a large-but-legitimately-uploaded STL would silently disappear from listings
    /// after the next backfill pass (caught in #1837 follow-up review by Bishop). The value below
    /// (600 MiB) is comfortably above that endpoint cap, so it only trips for files that could
    /// never have entered through the upload endpoint in the first place — i.e. it protects only
    /// the metadata backfill path, which reads whatever already exists on disk regardless of size
    /// and has no equivalent request-size gate of its own.
    /// </summary>
    private const long MaxStlFileSizeBytes = 600L * 1024 * 1024;

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

        if (extension == ".obj")
        {
            return await AnalyzeObjAsync(filePath, cancellationToken);
        }

        // Unsupported formats (PLY, STEP, ...) return null: unanalyzed, not invalid.
        return null;
    }

    private static async Task<ModelAnalysisResult?> AnalyzeStlAsync(string filePath, CancellationToken cancellationToken)
    {
        // Attempt to detect ASCII vs binary STL
        using FileStream fs = File.OpenRead(filePath);
        if (fs.Length < 84)
        {
            // Too small to contain even a binary STL header + triangle count: this is a
            // recognized STL upload that is structurally unreadable, not an unsupported format.
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["File is too small to be a valid STL (must be at least 84 bytes)"]);
        }

        if (fs.Length > MaxStlFileSizeBytes)
        {
            // Checked before any ASCII/binary parsing so an oversized file is never scanned:
            // mirrors the 3MF archive-size guard (MaxArchiveUncompressedBytes) so this ceiling
            // doesn't depend solely on the upload endpoint's request-size cap, and also protects
            // the backfill path, which reads arbitrary existing files from disk (#1837).
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["STL file exceeds the accepted size budget"]);
        }

        byte[] header = new byte[80];
        int bytesRead = await fs.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        if (bytesRead < header.Length)
        {
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["Failed to read STL header"]);
        }

        // Peek triangle count for binary (next 4 bytes)
        byte[] countBytes = new byte[4];
        int countRead = await fs.ReadAsync(countBytes.AsMemory(0, 4), cancellationToken);
        if (countRead < 4)
        {
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["Failed to read STL triangle count"]);
        }

        uint triangleCount = BitConverter.ToUInt32(countBytes, 0);

        // Simple heuristic: if header starts with "solid" then treat as ASCII for small files
        string headerString = Encoding.ASCII.GetString(header).Trim();
        _ = fs.Seek(0, SeekOrigin.Begin);

        // Binary STL files are sometimes authored with a "solid ..." text header for tooling
        // compatibility even though the rest of the file is binary triangle data, so a header
        // check alone is not reliable. A binary STL's file size is fully determined by its
        // declared triangle count (84-byte header/count + 50 bytes per triangle); when the
        // actual file size matches that formula exactly, treat it as binary regardless of the
        // header text. Only fall back to the ASCII parser when the size doesn't match and the
        // header looks like ASCII STL.
        long expectedBinarySize = 84L + (triangleCount * 50L);
        bool looksBinary = fs.Length == expectedBinarySize;

        // No file-size ceiling here: a legitimate ASCII STL can exceed any arbitrary size cutoff
        // (large, detailed prints routinely produce multi-hundred-MB ASCII files), and gating the
        // ASCII fallback by size would force such files down the binary parser, which would then
        // misinterpret plain text bytes as a binary triangle count/vertex stream.
        bool looksAscii = !looksBinary && headerString.StartsWith("solid", StringComparison.OrdinalIgnoreCase);

        if (looksAscii)
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
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or OverflowException)
            {
                return new ModelAnalysisResult(null, null, null, (int)actualTriangles, IsValid: false, ValidationErrors: ["Failed to read binary STL triangle data"]);
            }
        }
    }

    /// <summary>
    /// Analyzes a Wavefront OBJ file to extract vertex/face-derived bounding-box dimensions and a
    /// face count. OBJ is a plain-text format with no structural framing (unlike STL's triangle
    /// count or 3MF's ZIP/XML container), so "structurally unreadable" here means: no vertex data
    /// at all, a vertex/face line that cannot be parsed, or a face referencing a vertex index that
    /// doesn't exist (#1866) — not whether the geometry is watertight or printable (#1811/#1814).
    /// </summary>
    private static async Task<ModelAnalysisResult?> AnalyzeObjAsync(string filePath, CancellationToken cancellationToken)
    {
        using FileStream fs = File.OpenRead(filePath);
        using StreamReader sr = new(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        int vertexCount = 0;
        int faceCount = 0;
        string? line;
        while ((line = await sr.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            string[] parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            // "v" is an exact token match so this doesn't also match "vn" (normals) or
            // "vt" (texture coordinates), which have a different, unrelated float arity.
            if (parts[0] == "v")
            {
                if (parts.Length < 4 ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double vx) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double vy) ||
                    !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double vz))
                {
                    return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: [$"Malformed vertex line: '{trimmed}'"]);
                }

                vertexCount++;
                minX = Math.Min(minX, vx);
                minY = Math.Min(minY, vy);
                minZ = Math.Min(minZ, vz);
                maxX = Math.Max(maxX, vx);
                maxY = Math.Max(maxY, vy);
                maxZ = Math.Max(maxZ, vz);
            }
            else if (parts[0] == "f")
            {
                if (parts.Length < 4)
                {
                    return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: [$"Malformed face line (needs at least 3 vertex references): '{trimmed}'"]);
                }

                for (int i = 1; i < parts.Length; i++)
                {
                    // Face vertex refs may be "v", "v/vt", "v/vt/vn", or "v//vn"; only the first
                    // (vertex) index matters for structural validation.
                    string indexToken = parts[i].Split('/')[0];
                    if (!int.TryParse(indexToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawIndex) || rawIndex == 0)
                    {
                        return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: [$"Malformed face vertex index '{parts[i]}' in line: '{trimmed}'"]);
                    }

                    // OBJ indices are 1-based; negative indices are relative to the current vertex count.
                    int resolvedIndex = rawIndex > 0 ? rawIndex : vertexCount + rawIndex + 1;
                    if (resolvedIndex < 1 || resolvedIndex > vertexCount)
                    {
                        return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: [$"Face references vertex index {rawIndex}, which is out of range (mesh has {vertexCount} vertices so far)"]);
                    }
                }

                faceCount++;
            }
        }

        if (vertexCount == 0)
        {
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["No vertex data found in mesh"]);
        }

        if (double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(minZ))
        {
            return new ModelAnalysisResult(null, null, null, 0, IsValid: false, ValidationErrors: ["No vertex data found in mesh"]);
        }

        double dimX = maxX - minX;
        double dimY = maxY - minY;
        double dimZ = maxZ - minZ;

        // faceCount is a polygon count, not a strict triangle count (OBJ faces need not be
        // triangles), but it's the closest available analogue and is reported best-effort.
        return new ModelAnalysisResult(dimX, dimY, dimZ, faceCount);
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
        // The archive-level entry-count/byte-budget/compression-ratio checks in
        // AnalyzeThreeMfAsync rely on ZIP central-directory metadata (Length/CompressedLength),
        // which is declared by the archive itself and not independently verified against what
        // actually decompresses. MaxCharactersInDocument below is the authoritative guard against
        // a malicious entry whose declared size doesn't match its real decompressed size: the
        // reader aborts once it has read that many characters regardless of what the ZIP metadata
        // claimed. Do not remove or relax it when touching this method.
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
        catch (XmlException ex) when (ex.Message.Contains("DTD is prohibited", StringComparison.OrdinalIgnoreCase))
        {
            // .NET's XmlReader raises this specific, stable message only when DtdProcessing.Prohibit
            // rejects a DOCTYPE declaration before any entity reference in it could be resolved. Keeping
            // it as its own branch (rather than folding into the generic XmlException handler below)
            // lets tests assert that a DOCTYPE/XXE payload was specifically rejected for that reason,
            // not merely that some unrelated XML well-formedness error also happened to occur.
            return new ModelAnalysisResult(null, null, null, null, IsValid: false, ValidationErrors: ["Model part XML declares a prohibited DOCTYPE"]);
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
