using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;
using static Farm.OrcaSlicer.Worker.Services.ThreeMfProjectBuilder;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests for <see cref="ThreeMfProjectBuilder"/>.
/// Validates 3MF ZIP structure, mesh parsing, and transform matrix computation.
/// </summary>
public class ThreeMfProjectBuilderTests : IDisposable
{
    private static readonly XNamespace Ns3Mf = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"3mf-test-{Guid.NewGuid():N}");

    public ThreeMfProjectBuilderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    #region STL Helpers

    /// <summary>
    /// Create a minimal binary STL file representing a single triangle.
    /// </summary>
    private string CreateSingleTriangleStl(
        float x1 = 0, float y1 = 0, float z1 = 0,
        float x2 = 1, float y2 = 0, float z2 = 0,
        float x3 = 0, float y3 = 1, float z3 = 0,
        string? name = null)
    {
        string path = Path.Combine(_tempDir, name ?? $"model-{Guid.NewGuid():N}.stl");
        using FileStream fs = File.Create(path);
        using BinaryWriter bw = new(fs);

        // 80-byte header
        bw.Write(new byte[80]);
        // Triangle count
        bw.Write((uint)1);
        // Normal
        bw.Write(0f); bw.Write(0f); bw.Write(1f);
        // Vertices
        bw.Write(x1); bw.Write(y1); bw.Write(z1);
        bw.Write(x2); bw.Write(y2); bw.Write(z2);
        bw.Write(x3); bw.Write(y3); bw.Write(z3);
        // Attribute byte count
        bw.Write((ushort)0);

        return path;
    }

    /// <summary>
    /// Create a binary STL with two triangles sharing an edge (a simple quad).
    /// Total unique vertices: 4. Total triangles: 2.
    /// </summary>
    private string CreateTwoTriangleStl(string? name = null)
    {
        string path = Path.Combine(_tempDir, name ?? $"quad-{Guid.NewGuid():N}.stl");
        using FileStream fs = File.Create(path);
        using BinaryWriter bw = new(fs);

        bw.Write(new byte[80]);
        bw.Write((uint)2);

        // Triangle 1: (0,0,0), (1,0,0), (1,1,0)
        bw.Write(0f); bw.Write(0f); bw.Write(1f); // normal
        bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(1f); bw.Write(0f); bw.Write(0f);
        bw.Write(1f); bw.Write(1f); bw.Write(0f);
        bw.Write((ushort)0);

        // Triangle 2: (0,0,0), (1,1,0), (0,1,0) — shares edge (0,0,0)-(1,1,0)
        bw.Write(0f); bw.Write(0f); bw.Write(1f);
        bw.Write(0f); bw.Write(0f); bw.Write(0f);
        bw.Write(1f); bw.Write(1f); bw.Write(0f);
        bw.Write(0f); bw.Write(1f); bw.Write(0f);
        bw.Write((ushort)0);

        return path;
    }

    #endregion

    #region ParseBinaryStl Tests

    [Fact]
    public void ParseBinaryStl_SingleTriangle_ReturnsCorrectMesh()
    {
        string stl = CreateSingleTriangleStl();

        MeshData mesh = ThreeMfProjectBuilder.ParseBinaryStl(stl);

        mesh.Vertices.Should().HaveCount(3);
        mesh.Triangles.Should().HaveCount(1);
        mesh.Triangles[0].Should().Be((0, 1, 2));
    }

    [Fact]
    public void ParseBinaryStl_TwoTrianglesSharedVertices_DeduplicatesCorrectly()
    {
        string stl = CreateTwoTriangleStl();

        MeshData mesh = ThreeMfProjectBuilder.ParseBinaryStl(stl);

        mesh.Vertices.Should().HaveCount(4, "two shared vertices should be deduplicated");
        mesh.Triangles.Should().HaveCount(2);
    }

    [Fact]
    public void ParseBinaryStl_TooSmallFile_ThrowsInvalidOperation()
    {
        string path = Path.Combine(_tempDir, "tiny.stl");
        File.WriteAllBytes(path, new byte[10]);

        Action act = () => ThreeMfProjectBuilder.ParseBinaryStl(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
    }

    [Fact]
    public void ParseBinaryStl_SizeMismatch_ThrowsInvalidOperation()
    {
        string path = Path.Combine(_tempDir, "truncated.stl");
        using (FileStream fs = File.Create(path))
        using (BinaryWriter bw = new(fs))
        {
            bw.Write(new byte[80]);
            bw.Write((uint)100); // claims 100 triangles but file is too small
        }

        Action act = () => ThreeMfProjectBuilder.ParseBinaryStl(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*size mismatch*");
    }

    #endregion

    #region BuildTransformAttribute Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildTransformAttribute_NullOrEmpty_ReturnsEmpty(string? json)
    {
        ThreeMfProjectBuilder.BuildTransformAttribute(json).Should().BeEmpty();
    }

    [Fact]
    public void BuildTransformAttribute_IdentityTransform_ReturnsEmpty()
    {
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

        ThreeMfProjectBuilder.BuildTransformAttribute(json).Should().BeEmpty();
    }

    [Fact]
    public void BuildTransformAttribute_InvalidJson_ReturnsEmpty()
    {
        ThreeMfProjectBuilder.BuildTransformAttribute("not json{").Should().BeEmpty();
    }

    [Fact]
    public void BuildTransformAttribute_TranslationOnly_CorrectMatrix()
    {
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[10,20,30]}""";

        string attr = ThreeMfProjectBuilder.BuildTransformAttribute(json);
        double[] m = ParseTransformValues(attr);

        // Identity upper-left, translation in last row
        m[0].Should().BeApproximately(1, 1e-6);
        m[4].Should().BeApproximately(1, 1e-6);
        m[8].Should().BeApproximately(1, 1e-6);
        m[9].Should().BeApproximately(10, 1e-6);
        m[10].Should().BeApproximately(20, 1e-6);
        m[11].Should().BeApproximately(30, 1e-6);
    }

    [Fact]
    public void BuildTransformAttribute_UniformScale_CorrectDiagonal()
    {
        string json = """{"rotation":[0,0,0],"scale":[2,2,2],"position":[0,0,0]}""";

        string attr = ThreeMfProjectBuilder.BuildTransformAttribute(json);
        double[] m = ParseTransformValues(attr);

        m[0].Should().BeApproximately(2, 1e-6);
        m[4].Should().BeApproximately(2, 1e-6);
        m[8].Should().BeApproximately(2, 1e-6);
    }

    #endregion

    #region ComputeTransformMatrix Tests

    [Fact]
    public void ComputeTransformMatrix_Identity_ReturnsIdentityMatrix()
    {
        string result = ThreeMfProjectBuilder.ComputeTransformMatrix(0, 0, 0, 1, 1, 1, 0, 0, 0);

        double[] m = ParseTransformValues(result);
        m.Should().BeEquivalentTo(
            new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
            opts => opts.Using<double>(ctx =>
                ctx.Subject.Should().BeApproximately(ctx.Expectation, 1e-9))
                .WhenTypeIs<double>());
    }

    [Fact]
    public void ComputeTransformMatrix_90DegreeZRotation_CorrectMatrix()
    {
        double rz = Math.PI / 2;

        string result = ThreeMfProjectBuilder.ComputeTransformMatrix(0, 0, rz, 1, 1, 1, 0, 0, 0);
        double[] m = ParseTransformValues(result);

        // Row-vector convention: 90° Z rotation
        // m00 = cos(90°) ≈ 0, m01 = sin(90°) ≈ 1, m02 = 0
        // m10 = -sin(90°) ≈ -1, m11 = cos(90°) ≈ 0, m12 = 0
        // m20 = 0, m21 = 0, m22 = 1
        m[0].Should().BeApproximately(0, 1e-9);
        m[1].Should().BeApproximately(1, 1e-9);
        m[2].Should().BeApproximately(0, 1e-9);
        m[3].Should().BeApproximately(-1, 1e-9);
        m[4].Should().BeApproximately(0, 1e-9);
        m[5].Should().BeApproximately(0, 1e-9);
        m[6].Should().BeApproximately(0, 1e-9);
        m[7].Should().BeApproximately(0, 1e-9);
        m[8].Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void ComputeTransformMatrix_90DegreeXRotation_CorrectMatrix()
    {
        double rx = Math.PI / 2;

        string result = ThreeMfProjectBuilder.ComputeTransformMatrix(rx, 0, 0, 1, 1, 1, 0, 0, 0);
        double[] m = ParseTransformValues(result);

        // Row-vector convention: 90° X rotation
        // m00 = 1, m01 = 0, m02 = 0
        // m10 = 0, m11 = cos(90°) ≈ 0, m12 = sin(90°) ≈ 1
        // m20 = 0, m21 = -sin(90°) ≈ -1, m22 = cos(90°) ≈ 0
        m[0].Should().BeApproximately(1, 1e-9);
        m[3].Should().BeApproximately(0, 1e-9);
        m[4].Should().BeApproximately(0, 1e-9);
        m[5].Should().BeApproximately(1, 1e-9);
        m[7].Should().BeApproximately(-1, 1e-9);
        m[8].Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void ComputeTransformMatrix_NonUniformScale_CorrectDiagonal()
    {
        string result = ThreeMfProjectBuilder.ComputeTransformMatrix(0, 0, 0, 2, 3, 4, 0, 0, 0);
        double[] m = ParseTransformValues(result);

        m[0].Should().BeApproximately(2, 1e-9);
        m[4].Should().BeApproximately(3, 1e-9);
        m[8].Should().BeApproximately(4, 1e-9);
        // off-diagonals should be zero
        m[1].Should().BeApproximately(0, 1e-9);
        m[2].Should().BeApproximately(0, 1e-9);
        m[3].Should().BeApproximately(0, 1e-9);
        m[5].Should().BeApproximately(0, 1e-9);
        m[6].Should().BeApproximately(0, 1e-9);
        m[7].Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void ComputeTransformMatrix_TranslationValues_InLastRow()
    {
        string result = ThreeMfProjectBuilder.ComputeTransformMatrix(0, 0, 0, 1, 1, 1, 5, 10, 15);
        double[] m = ParseTransformValues(result);

        m[9].Should().BeApproximately(5, 1e-9);
        m[10].Should().BeApproximately(10, 1e-9);
        m[11].Should().BeApproximately(15, 1e-9);
    }

    #endregion

    #region Build (end-to-end) Tests

    [Fact]
    public void Build_SingleModel_NoTransform_ProducesValid3Mf()
    {
        string stl = CreateSingleTriangleStl();
        var models = new[] { new ModelEntry(stl, null) };

        string path = ThreeMfProjectBuilder.Build(models, _tempDir);

        File.Exists(path).Should().BeTrue();
        Path.GetExtension(path).Should().Be(".3mf");

        XDocument modelDoc = Extract3DModelXml(path);
        var objects = modelDoc.Descendants(Ns3Mf + "object").ToList();
        objects.Should().HaveCount(1);

        var items = modelDoc.Descendants(Ns3Mf + "item").ToList();
        items.Should().HaveCount(1);
        items[0].Attribute("transform").Should().BeNull("no transform for null input");
    }

    [Fact]
    public void Build_TwoModels_WithTransforms_ProducesCorrect3Mf()
    {
        string stl1 = CreateSingleTriangleStl(name: "model1.stl");
        string stl2 = CreateSingleTriangleStl(x1: 5, name: "model2.stl");

        string tf1 = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[10,0,0]}""";
        string tf2 = """{"rotation":[0,0,1.5707963],"scale":[2,2,2],"position":[0,20,0]}""";

        var models = new[]
        {
            new ModelEntry(stl1, tf1),
            new ModelEntry(stl2, tf2)
        };

        string path = ThreeMfProjectBuilder.Build(models, _tempDir);

        XDocument modelDoc = Extract3DModelXml(path);
        var objects = modelDoc.Descendants(Ns3Mf + "object").ToList();
        objects.Should().HaveCount(2);

        var items = modelDoc.Descendants(Ns3Mf + "item").ToList();
        items.Should().HaveCount(2);

        items[0].Attribute("transform").Should().NotBeNull("model 1 has translation");
        items[1].Attribute("transform").Should().NotBeNull("model 2 has rotation + scale");
    }

    [Fact]
    public void Build_EmptyModels_ThrowsArgumentException()
    {
        Action act = () => ThreeMfProjectBuilder.Build(Array.Empty<ModelEntry>(), _tempDir);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_3MfZipContainsRequiredEntries()
    {
        string stl = CreateSingleTriangleStl();
        string path = ThreeMfProjectBuilder.Build([new ModelEntry(stl, null)], _tempDir);

        using ZipArchive zip = ZipFile.OpenRead(path);
        var entryNames = zip.Entries.Select(e => e.FullName).ToList();

        entryNames.Should().Contain("[Content_Types].xml");
        entryNames.Should().Contain("_rels/.rels");
        entryNames.Should().Contain("3D/3dmodel.model");
    }

    [Fact]
    public void Build_MeshDataPreservedInXml()
    {
        string stl = CreateTwoTriangleStl();
        string path = ThreeMfProjectBuilder.Build([new ModelEntry(stl, null)], _tempDir);

        XDocument doc = Extract3DModelXml(path);
        var vertices = doc.Descendants(Ns3Mf + "vertex").ToList();
        var triangles = doc.Descendants(Ns3Mf + "triangle").ToList();

        vertices.Should().HaveCount(4, "quad has 4 unique vertices");
        triangles.Should().HaveCount(2, "quad has 2 triangles");
    }

    #endregion

    #region Helpers

    private static double[] ParseTransformValues(string attr)
    {
        return attr.Split(' ').Select(s => double.Parse(s, Inv)).ToArray();
    }

    private static XDocument Extract3DModelXml(string threeMfPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(threeMfPath);
        ZipArchiveEntry? entry = zip.GetEntry("3D/3dmodel.model");
        entry.Should().NotBeNull();
        using Stream stream = entry!.Open();
        return XDocument.Load(stream);
    }

    #endregion
}
