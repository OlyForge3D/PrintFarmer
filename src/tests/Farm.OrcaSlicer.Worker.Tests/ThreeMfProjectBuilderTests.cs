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
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"3mf-test-{Guid.NewGuid():N}");

    public ThreeMfProjectBuilderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
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
        string path = Path.Join(_tempDir, name ?? $"model-{Guid.NewGuid():N}.stl");
        using FileStream fs = File.Create(path);
        using BinaryWriter bw = new(fs);

        // 80-byte header
        bw.Write(new byte[80]);
        // Triangle count
        bw.Write((uint)1);
        // Normal
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f);
        // Vertices
        bw.Write(x1);
        bw.Write(y1);
        bw.Write(z1);
        bw.Write(x2);
        bw.Write(y2);
        bw.Write(z2);
        bw.Write(x3);
        bw.Write(y3);
        bw.Write(z3);
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
        string path = Path.Join(_tempDir, name ?? $"quad-{Guid.NewGuid():N}.stl");
        using FileStream fs = File.Create(path);
        using BinaryWriter bw = new(fs);

        bw.Write(new byte[80]);
        bw.Write((uint)2);

        // Triangle 1: (0,0,0), (1,0,0), (1,1,0)
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f); // normal
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f);
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f);
        bw.Write(1f);
        bw.Write(0f);
        bw.Write((ushort)0);

        // Triangle 2: (0,0,0), (1,1,0), (0,1,0) — shares edge (0,0,0)-(1,1,0)
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f);
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f);
        bw.Write(1f);
        bw.Write(0f);
        bw.Write(0f);
        bw.Write(1f);
        bw.Write(0f);
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
        string path = Path.Join(_tempDir, "tiny.stl");
        File.WriteAllBytes(path, new byte[10]);

        Action act = () => ThreeMfProjectBuilder.ParseBinaryStl(path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*too small*");
    }

    [Fact]
    public void ParseBinaryStl_SizeMismatch_ThrowsInvalidOperation()
    {
        string path = Path.Join(_tempDir, "truncated.stl");
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

    #region BuildItemTransform Tests

    private static readonly MeshBounds OriginBounds = new(0, 0, 0, 0);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildItemTransform_NullOrEmpty_PlacesModelAtBedCenter(string? json)
    {
        double[] m = ParseTransformValues(BuildItemTransform(json, OriginBounds, (110, 110)));

        // Identity orientation, translated to the bed centre — a model with no transform still
        // has to be moved onto the bed, because 3MF build items are in bed coordinates.
        m[0].Should().BeApproximately(1, 1e-6);
        m[4].Should().BeApproximately(1, 1e-6);
        m[8].Should().BeApproximately(1, 1e-6);
        m[9].Should().BeApproximately(110, 1e-6);
        m[10].Should().BeApproximately(110, 1e-6);
    }

    [Fact]
    public void BuildItemTransform_IdentityTransform_PlacesModelAtBedCenter()
    {
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

        double[] m = ParseTransformValues(BuildItemTransform(json, OriginBounds, (125, 105)));

        m[9].Should().BeApproximately(125, 1e-6);
        m[10].Should().BeApproximately(105, 1e-6);
    }

    [Fact]
    public void BuildItemTransform_InvalidJson_PlacesModelAtBedCenter()
    {
        double[] m = ParseTransformValues(BuildItemTransform("not json{", OriginBounds, (110, 110)));

        m[0].Should().BeApproximately(1, 1e-6);
        m[9].Should().BeApproximately(110, 1e-6);
        m[10].Should().BeApproximately(110, 1e-6);
    }

    [Fact]
    public void BuildItemTransform_WorkspacePosition_IsRelativeToBedCenter()
    {
        // The React workspace draws the bed centred on the world origin, so a model at
        // position [30, 0] is 30 mm right of the bed centre — not 30 mm from the bed corner.
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[30,0,0]}""";

        double[] m = ParseTransformValues(BuildItemTransform(json, OriginBounds, (110, 110)));

        m[9].Should().BeApproximately(140, 1e-6);
        m[10].Should().BeApproximately(110, 1e-6);
    }

    [Fact]
    public void BuildItemTransform_OffOriginMesh_IsRecenteredOnItsOwnBoundingBox()
    {
        // The viewer recentres every mesh on its bounding box before positioning it, so an STL
        // authored far from its own origin must still land on the requested point.
        var bounds = new MeshBounds(CenterX: 500, CenterY: -200, CenterZ: 10, HalfHeight: 10);
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

        double[] m = ParseTransformValues(BuildItemTransform(json, bounds, (110, 110)));

        m[9].Should().BeApproximately(110 - 500, 1e-6);
        m[10].Should().BeApproximately(110 + 200, 1e-6);
    }

    [Fact]
    public void BuildItemTransform_ZeroZPosition_SitsOnTheBed()
    {
        // position.z == 0 means "sitting on the bed": the mesh centre is lifted by half the
        // model height so its lowest point is at Z=0.
        var bounds = new MeshBounds(CenterX: 0, CenterY: 0, CenterZ: 12, HalfHeight: 12);
        string json = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

        double[] m = ParseTransformValues(BuildItemTransform(json, bounds, (110, 110)));

        // Raw mesh spans Z 0..24; after the transform its bottom must be at Z=0.
        m[11].Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void BuildItemTransform_ScaleAndRotation_AppliedAboutTheMeshCenter()
    {
        // 90° about Z with 2x scale, mesh centred at (10, 0, 0) in its own coordinates.
        var bounds = new MeshBounds(CenterX: 10, CenterY: 0, CenterZ: 0, HalfHeight: 0);
        string json = """{"rotation":[0,0,1.5707963267948966],"scale":[2,2,2],"position":[0,0,0]}""";

        double[] m = ParseTransformValues(BuildItemTransform(json, bounds, (110, 110)));

        // The mesh centre must map exactly onto the bed centre regardless of rotation/scale.
        (double X, double Y, double _) = MapPoint(m, 10, 0, 0);
        X.Should().BeApproximately(110, 1e-6);
        Y.Should().BeApproximately(110, 1e-6);

        // Linear part is still Scale × Rotate(90° about Z).
        m[0].Should().BeApproximately(0, 1e-6);
        m[1].Should().BeApproximately(2, 1e-6);
        m[3].Should().BeApproximately(-2, 1e-6);
        m[4].Should().BeApproximately(0, 1e-6);
    }

    #endregion

    #region ComputeBounds Tests

    [Fact]
    public void ComputeBounds_EmptyMesh_ReturnsZeroBounds()
    {
        MeshBounds bounds = ThreeMfProjectBuilder.ComputeBounds(
            new MeshData(Array.Empty<(float, float, float)>(), Array.Empty<(int, int, int)>()));

        bounds.Should().Be(new MeshBounds(0, 0, 0, 0));
    }

    [Fact]
    public void ComputeBounds_SingleTriangle_ReturnsBoundingBoxCenter()
    {
        // Triangle (0,0,0), (1,0,0), (0,1,0) → bbox 0..1 on X and Y, flat in Z.
        MeshData mesh = ThreeMfProjectBuilder.ParseBinaryStl(CreateSingleTriangleStl());

        MeshBounds bounds = ThreeMfProjectBuilder.ComputeBounds(mesh);

        bounds.CenterX.Should().BeApproximately(0.5, 1e-6);
        bounds.CenterY.Should().BeApproximately(0.5, 1e-6);
        bounds.CenterZ.Should().BeApproximately(0, 1e-6);
        bounds.HalfHeight.Should().BeApproximately(0, 1e-6);
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

    private static readonly (double X, double Y) TestBedCenter = (110, 110);

    [Fact]
    public void Build_SingleModel_NoTransform_ProducesValid3Mf()
    {
        string stl = CreateSingleTriangleStl();
        var models = new[] { new ModelEntry(stl, null) };

        string path = ThreeMfProjectBuilder.Build(models, _tempDir, TestBedCenter);

        File.Exists(path).Should().BeTrue();
        Path.GetExtension(path).Should().Be(".3mf");

        XDocument modelDoc = Extract3DModelXml(path);
        var objects = modelDoc.Descendants(Ns3Mf + "object").ToList();
        objects.Should().HaveCount(1);

        var items = modelDoc.Descendants(Ns3Mf + "item").ToList();
        items.Should().HaveCount(1);
        items[0].Attribute("transform").Should().NotBeNull(
            "even an untransformed model must be translated onto the bed");
    }

    /// <summary>
    /// Regression for #1794: a SINGLE model with a custom position must go through the 3MF
    /// project so its placement survives — there is no CLI flag that can express it.
    /// </summary>
    [Fact]
    public void Build_SingleModel_WithPosition_EmbedsBedRelativePlacement()
    {
        string stl = CreateSingleTriangleStl();
        string tf = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[30,0,0]}""";

        string path = ThreeMfProjectBuilder.Build([new ModelEntry(stl, tf)], _tempDir, TestBedCenter);

        XDocument modelDoc = Extract3DModelXml(path);
        var items = modelDoc.Descendants(Ns3Mf + "item").ToList();
        items.Should().HaveCount(1);

        double[] m = ParseTransformValues(items[0].Attribute("transform")!.Value);

        // Mesh bbox centre is (0.5, 0.5, 0); it must land at bed centre + workspace position.
        (double X, double Y, double _) = MapPoint(m, 0.5, 0.5, 0);
        X.Should().BeApproximately(140, 1e-6);
        Y.Should().BeApproximately(110, 1e-6);
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

        string path = ThreeMfProjectBuilder.Build(models, _tempDir, TestBedCenter);

        XDocument modelDoc = Extract3DModelXml(path);
        var objects = modelDoc.Descendants(Ns3Mf + "object").ToList();
        objects.Should().HaveCount(2);

        var items = modelDoc.Descendants(Ns3Mf + "item").ToList();
        items.Should().HaveCount(2);

        items[0].Attribute("transform").Should().NotBeNull("model 1 has translation");
        items[1].Attribute("transform").Should().NotBeNull("model 2 has rotation + scale");

        // The two models keep their relative layout: 10 mm right vs 20 mm back of bed centre.
        // model1 mesh bbox centre is (0.5, 0.5, 0); model2's is (2.5, 0.5, 0).
        double[] m1 = ParseTransformValues(items[0].Attribute("transform")!.Value);
        double[] m2 = ParseTransformValues(items[1].Attribute("transform")!.Value);

        (double X1, double Y1, double _) = MapPoint(m1, 0.5, 0.5, 0);
        X1.Should().BeApproximately(120, 1e-6);
        Y1.Should().BeApproximately(110, 1e-6);

        (double X2, double Y2, double _) = MapPoint(m2, 2.5, 0.5, 0);
        X2.Should().BeApproximately(110, 1e-6);
        Y2.Should().BeApproximately(130, 1e-6);
    }

    [Fact]
    public void Build_EmptyModels_ThrowsArgumentException()
    {
        Action act = () => ThreeMfProjectBuilder.Build(Array.Empty<ModelEntry>(), _tempDir, TestBedCenter);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_3MfZipContainsRequiredEntries()
    {
        string stl = CreateSingleTriangleStl();
        string path = ThreeMfProjectBuilder.Build([new ModelEntry(stl, null)], _tempDir, TestBedCenter);

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
        string path = ThreeMfProjectBuilder.Build([new ModelEntry(stl, null)], _tempDir, TestBedCenter);

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

    /// <summary>
    /// Apply a parsed 3MF transform (row-vector convention) to a point.
    /// </summary>
    private static (double X, double Y, double Z) MapPoint(double[] m, double x, double y, double z) =>
        ((x * m[0]) + (y * m[3]) + (z * m[6]) + m[9],
         (x * m[1]) + (y * m[4]) + (z * m[7]) + m[10],
         (x * m[2]) + (y * m[5]) + (z * m[8]) + m[11]);

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
