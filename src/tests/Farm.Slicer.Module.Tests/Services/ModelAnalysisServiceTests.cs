using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Models;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Tests for <see cref="ModelAnalysisService"/> covering binary STL, ASCII STL, and 3MF geometry
/// extraction (#1814). Before this fix every model reported dimensionX/Y/Z and triangleCount as
/// null: 3MF was entirely unsupported, and STL extension matching was case-sensitive so uploads
/// preserving their original casing (e.g. "Model.STL") silently skipped analysis too.
/// </summary>
public class ModelAnalysisServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-analysis-tests", Guid.NewGuid().ToString());
    private readonly ModelAnalysisService _sut = new();

    public ModelAnalysisServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    private string WriteFile(string fileName, byte[] content)
    {
        string path = Path.Join(_tempDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ---- Binary STL -------------------------------------------------------

    [Fact]
    public async Task AnalyzeModelAsync_ValidBinaryStl_ReturnsGeometryAndIsValidTrue()
    {
        byte[] bytes = BuildBinaryStl(triangleCount: 4);
        string path = WriteFile("valid.stl", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".stl", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(4, result!.TriangleCount);
        Assert.True(result.IsValid);
        Assert.Null(result.ValidationErrors);
        Assert.NotNull(result.DimensionX);
        Assert.NotNull(result.DimensionY);
        Assert.NotNull(result.DimensionZ);
    }

    [Fact]
    public async Task AnalyzeModelAsync_TruncatedBinaryStl_ReturnsIsValidFalseWithActualTriangleCount()
    {
        byte[] bytes = BuildBinaryStl(triangleCount: 10, truncateAfterTriangles: 3);
        string path = WriteFile("truncated.stl", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".stl", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.TriangleCount);
        Assert.False(result.IsValid);
        Assert.NotNull(result.ValidationErrors);
        Assert.Contains(result.ValidationErrors!, e => e.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnalyzeModelAsync_EmptyBinaryStl_ReturnsIsValidFalse()
    {
        byte[] bytes = BuildBinaryStl(triangleCount: 0);
        string path = WriteFile("empty.stl", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".stl", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TriangleCount);
        Assert.False(result.IsValid);
        Assert.Null(result.DimensionX);
    }

    [Fact]
    public async Task AnalyzeModelAsync_UppercaseStlExtension_IsStillAnalyzed()
    {
        // Regression: extension comparison used to be case-sensitive, so uploads that preserved
        // their original casing (e.g. "Model.STL") silently skipped analysis entirely (#1814).
        byte[] bytes = BuildBinaryStl(triangleCount: 2);
        string path = WriteFile("valid2.stl", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".STL", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.TriangleCount);
    }

    // ---- ASCII STL ----------------------------------------------------------

    [Fact]
    public async Task AnalyzeModelAsync_ValidAsciiStl_ReturnsGeometryAndIsValidTrue()
    {
        string ascii =
            "solid test\n" +
            "facet normal 0 0 1\n" +
            "outer loop\n" +
            "vertex 0 0 0\n" +
            "vertex 10 0 0\n" +
            "vertex 0 10 0\n" +
            "endloop\n" +
            "endfacet\n" +
            "facet normal 0 0 1\n" +
            "outer loop\n" +
            "vertex 0 0 0\n" +
            "vertex 0 10 0\n" +
            "vertex 0 0 5\n" +
            "endloop\n" +
            "endfacet\n" +
            "endsolid test\n";
        string path = WriteFile("valid_ascii.stl", Encoding.ASCII.GetBytes(ascii));

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".stl", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.TriangleCount);
        Assert.True(result.IsValid);
        Assert.Equal(10, result.DimensionX);
        Assert.Equal(10, result.DimensionY);
        Assert.Equal(5, result.DimensionZ);
    }

    [Fact]
    public async Task AnalyzeModelAsync_AsciiStlWithNoVertices_ReturnsIsValidFalse()
    {
        // Padded past the format's minimum-size sniff threshold; content itself has no vertex lines.
        string ascii = "solid empty\n" +
            string.Concat(Enumerable.Repeat("REM padding line so this file exceeds the format sniff threshold\n", 3)) +
            "endsolid empty\n";
        string path = WriteFile("empty_ascii.stl", Encoding.ASCII.GetBytes(ascii));

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".stl", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TriangleCount);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task AnalyzeModelAsync_UppercaseThreeMfExtension_IsStillAnalyzed()
    {
        byte[] bytes = BuildThreeMf(SimpleTetrahedronModelXml());
        string path = WriteFile("valid2.3mf", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".3MF", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(4, result!.TriangleCount);
    }

    // ---- 3MF ----------------------------------------------------------------

    [Fact]
    public async Task AnalyzeModelAsync_ValidThreeMf_ReturnsGeometryAndIsValidTrue()
    {
        byte[] bytes = BuildThreeMf(SimpleTetrahedronModelXml());
        string path = WriteFile("valid.3mf", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".3mf", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(4, result!.TriangleCount);
        Assert.True(result.IsValid);
        Assert.Null(result.ValidationErrors);
        Assert.Equal(10, result.DimensionX);
        Assert.Equal(10, result.DimensionY);
        Assert.Equal(10, result.DimensionZ);
    }

    [Fact]
    public async Task AnalyzeModelAsync_CorruptThreeMfArchive_ReturnsIsValidFalse()
    {
        // Not a real zip at all.
        byte[] bytes = Encoding.UTF8.GetBytes("this is not a zip archive");
        string path = WriteFile("corrupt.3mf", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".3mf", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.NotNull(result.ValidationErrors);
        Assert.Null(result.TriangleCount);
    }

    [Fact]
    public async Task AnalyzeModelAsync_ThreeMfWithoutModelPart_ReturnsIsValidFalse()
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("_rels/.rels");
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream, Encoding.UTF8);
            writer.Write("<Relationships/>");
        }

        string path = WriteFile("no_model_part.3mf", ms.ToArray());

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".3mf", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
        Assert.NotNull(result.ValidationErrors);
    }

    [Fact]
    public async Task AnalyzeModelAsync_ThreeMfWithEmptyMesh_ReturnsIsValidFalse()
    {
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<model xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
            "<resources><object id=\"1\" type=\"model\"><mesh>" +
            "<vertices></vertices><triangles></triangles>" +
            "</mesh></object></resources>" +
            "<build><item objectid=\"1\"/></build>" +
            "</model>";
        byte[] bytes = BuildThreeMf(xml);
        string path = WriteFile("empty_mesh.3mf", bytes);

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".3mf", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsValid);
    }

    // ---- Unsupported formats --------------------------------------------

    [Fact]
    public async Task AnalyzeModelAsync_UnsupportedExtension_ReturnsNull()
    {
        string path = WriteFile("model.obj", Encoding.UTF8.GetBytes("v 0 0 0\n"));

        ModelAnalysisResult? result = await _sut.AnalyzeModelAsync(path, ".obj", CancellationToken.None);

        Assert.Null(result);
    }

    // ---- Test fixture builders --------------------------------------------

    private static byte[] BuildBinaryStl(int triangleCount, int? truncateAfterTriangles = null)
    {
        using MemoryStream ms = new();
        using (BinaryWriter bw = new(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(new byte[80]); // header, not "solid"-prefixed so it is treated as binary
            bw.Write((uint)triangleCount);

            int trianglesToWrite = truncateAfterTriangles ?? triangleCount;
            for (int i = 0; i < trianglesToWrite; i++)
            {
                // Normal vector (ignored by the analyzer).
                bw.Write(0f);
                bw.Write(0f);
                bw.Write(1f);

                // Three vertices, spread out so the bounding box is non-trivial.
                for (int v = 0; v < 3; v++)
                {
                    bw.Write((float)(i + v));
                    bw.Write((float)(i + v) * 2);
                    bw.Write((float)(i + v) * 3);
                }

                bw.Write((ushort)0); // attribute byte count
            }
        }

        return ms.ToArray();
    }

    private static byte[] BuildThreeMf(string modelXml)
    {
        using MemoryStream ms = new();
        using (ZipArchive archive = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("3D/3dmodel.model");
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream, Encoding.UTF8);
            writer.Write(modelXml);
        }

        return ms.ToArray();
    }

    private static string SimpleTetrahedronModelXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<model xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
        "<resources><object id=\"1\" type=\"model\"><mesh>" +
        "<vertices>" +
        "<vertex x=\"0\" y=\"0\" z=\"0\"/>" +
        "<vertex x=\"10\" y=\"0\" z=\"0\"/>" +
        "<vertex x=\"0\" y=\"10\" z=\"0\"/>" +
        "<vertex x=\"0\" y=\"0\" z=\"10\"/>" +
        "</vertices>" +
        "<triangles>" +
        "<triangle v1=\"0\" v2=\"1\" v3=\"2\"/>" +
        "<triangle v1=\"0\" v2=\"1\" v3=\"3\"/>" +
        "<triangle v1=\"0\" v2=\"2\" v3=\"3\"/>" +
        "<triangle v1=\"1\" v2=\"2\" v3=\"3\"/>" +
        "</triangles>" +
        "</mesh></object></resources>" +
        "<build><item objectid=\"1\"/></build>" +
        "</model>";
}
