using System.Buffers.Binary;
using System.Text;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Adversarial tests for the calibration model validator: malicious meshes, archives and identities
/// must be rejected before any plan is compiled.
/// </summary>
public sealed class CalibrationModelValidatorTests
{
    private static CalibrationSpecification VerificationSpecification() =>
        CalibrationGenerationPipeline
            .CompileSpecification(CalibrationMethod.FinalVerification)
            .Value!;

    private static CalibrationSpecification TowerSpecification() =>
        CalibrationGenerationPipeline
            .CompileSpecification(CalibrationMethod.Temperature)
            .Value!;

    [Fact]
    public async Task ValidateGeneratedGeometryAsync_WithCanonicalBinaryStl_ComputesActualBounds()
    {
        byte[] content = CalibrationGenerationTestData.BinaryStlCuboid(20f, 30f, 10f);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(content, "calibration-body.stl"),
                TowerSpecification(),
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Bounds.SizeX.Should().Be(20m);
        _ = result.Value.Bounds.SizeY.Should().Be(30m);
        _ = result.Value.Bounds.SizeZ.Should().Be(10m);
        _ = result.Value.Provenance.Should().Be("generated");
        _ = result.Value.TriangleCount.Should().Be(4);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("C:\\windows\\system32\\config")]
    [InlineData("/etc/shadow")]
    [InlineData("cube.stl; rm -rf /")]
    public async Task ValidateGeneratedGeometryAsync_WithPathLikeFileName_RejectsUnsafeInput(
        string fileName)
    {
        byte[] content = CalibrationGenerationTestData.BinaryStlCuboid(20f, 20f, 10f);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(content, fileName),
                TowerSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_input_not_allowed");
    }

    [Fact]
    public async Task ValidateGeneratedGeometryAsync_WithModelLargerThanTheBed_Rejects()
    {
        byte[] content = CalibrationGenerationTestData.BinaryStlCuboid(400f, 400f, 10f);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(content, "oversize.stl"),
                TowerSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_outside_build_volume");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithMatchingIdentityAndDigest_PreservesProvenance()
    {
        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    CalibrationGenerationPipeline.ModelContent,
                    CalibrationModelFormats.Stl),
                VerificationSpecification(),
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Model3DId.Should().Be(CalibrationGenerationTestData.ModelId);
        _ = result.Value.Provenance.Should().Be("imported");
        _ = result.Value.Sha256.Should().Be(CalibrationGenerationPipeline.ModelSha256);
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithTamperedContent_RejectsOnDigestMismatch()
    {
        byte[] tampered = (byte[])CalibrationGenerationPipeline.ModelContent.Clone();
        tampered[90] ^= 0xFF;

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    tampered,
                    CalibrationModelFormats.Stl,
                    CalibrationGenerationPipeline.ModelSha256),
                VerificationSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("model_hash_mismatch");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithDifferentModelIdentity_Rejects()
    {
        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    Guid.NewGuid(),
                    CalibrationGenerationPipeline.ModelContent,
                    CalibrationModelFormats.Stl),
                VerificationSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should().Contain("linked_asset_mismatch");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithMissingProvenance_Rejects()
    {
        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    CalibrationGenerationPipeline.ModelContent,
                    CalibrationModelFormats.Stl,
                    provenance: null),
                VerificationSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_provenance_missing");
    }

    [Theory]
    [InlineData("obj")]
    [InlineData("gcode")]
    [InlineData("STL")]
    [InlineData("")]
    public async Task ValidateImportedAssetAsync_WithNonCanonicalFormatToken_Rejects(string format)
    {
        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    CalibrationGenerationPipeline.ModelContent,
                    format),
                VerificationSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_format_unsupported");
    }

    [Fact]
    public async Task ValidateGeneratedGeometryAsync_WithLyingTriangleCount_RejectsMalformedMesh()
    {
        byte[] content = CalibrationGenerationTestData.BinaryStlCuboid(20f, 20f, 10f);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(80, 4), 1_000_000);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(content, "lying.stl"),
                TowerSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_content_invalid");
    }

    [Fact]
    public async Task ValidateGeneratedGeometryAsync_WithNonFiniteVertices_Rejects()
    {
        byte[] content = CalibrationGenerationTestData.BinaryStlCuboid(20f, 20f, 10f);
        BinaryPrimitives.WriteSingleLittleEndian(content.AsSpan(96, 4), float.PositiveInfinity);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateGeneratedGeometryAsync(
                new CalibrationGeneratedGeometry(content, "infinite.stl"),
                TowerSpecification(),
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_content_invalid");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithWellFormed3mfPackage_ComputesBounds()
    {
        byte[] package = CalibrationGenerationTestData.ThreeMfCube();
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "cube.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Unit.Should().Be("millimeter");
        _ = result.Value.TriangleCount.Should().Be(2);
    }

    [Theory]
    [InlineData("micron")]
    [InlineData("inch")]
    [InlineData("meter")]
    public async Task ValidateImportedAssetAsync_With3mfDeclaringNonMillimetreUnits_Rejects(
        string unit)
    {
        byte[] package = CalibrationGenerationTestData.ThreeMfCube(unit);
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "cube.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_unit_unsupported");
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("3D/../../escape.model")]
    public async Task ValidateImportedAssetAsync_With3mfEntryTraversal_Rejects(string entryName)
    {
        byte[] package = CalibrationGenerationTestData.ZipPackage(
        [
            (entryName, Encoding.UTF8.GetBytes("payload")),
            ("3D/3dmodel.model", Encoding.UTF8.GetBytes("<model/>")),
        ]);
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "traversal.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_archive_path_traversal");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_With3mfUnsupportedResource_Rejects()
    {
        byte[] package = CalibrationGenerationTestData.ZipPackage(
        [
            ("scripts/postprocess.sh", Encoding.UTF8.GetBytes("#!/bin/sh\ncurl http://10.0.0.1")),
            ("3D/3dmodel.model", Encoding.UTF8.GetBytes("<model/>")),
        ]);
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "resource.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_archive_unsupported_resource");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithDecompressionBomb_Rejects()
    {
        byte[] bomb = new byte[64 * 1024 * 1024];
        byte[] package = CalibrationGenerationTestData.ZipPackage(
        [
            ("3D/3dmodel.model", bomb),
        ]);
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "bomb.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_archive_decompression_bomb");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithExternalEntityXml_RejectsUnsafeXml()
    {
        string malicious =
            "<?xml version=\"1.0\"?><!DOCTYPE model [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
            "<model unit=\"millimeter\" " +
            "xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
            "<resources><object id=\"1\"><mesh><vertices>" +
            "<vertex x=\"0\" y=\"0\" z=\"0\"/></vertices><triangles/></mesh></object></resources>" +
            "<build/></model>";
        byte[] package = CalibrationGenerationTestData.ZipPackage(
        [
            ("3D/3dmodel.model", Encoding.UTF8.GetBytes(malicious)),
        ]);
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "xxe.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_archive_xml_unsafe");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_With3mfDeclaringAnUnsupportedTransform_Rejects()
    {
        string model =
            "<?xml version=\"1.0\"?><model unit=\"millimeter\" " +
            "xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
            "<resources><object id=\"1\"><mesh><vertices>" +
            "<vertex x=\"0\" y=\"0\" z=\"0\"/><vertex x=\"1\" y=\"0\" z=\"0\"/>" +
            "<vertex x=\"1\" y=\"1\" z=\"0\"/></vertices>" +
            "<triangles><triangle v1=\"0\" v2=\"1\" v3=\"2\"/></triangles></mesh></object>" +
            "</resources><build><item objectid=\"1\" transform=\"not-a-matrix\"/></build></model>";
        byte[] package = CalibrationGenerationTestData.ZipPackage(
        [
            ("3D/3dmodel.model", Encoding.UTF8.GetBytes(model)),
        ]);
        CalibrationSpecification specification = ThreeMfSpecification(package);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    package,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "transform.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_transform_unsupported");
    }

    [Fact]
    public async Task ValidateImportedAssetAsync_WithArchiveThatIsNotAZip_Rejects()
    {
        byte[] content = Encoding.UTF8.GetBytes("not really a package");
        CalibrationSpecification specification = ThreeMfSpecification(content);

        CalibrationGenerationResult<CalibrationValidatedModel> result =
            await new CalibrationModelValidator().ValidateImportedAssetAsync(
                new FakeModelContentSource(
                    CalibrationGenerationTestData.ModelId,
                    content,
                    CalibrationModelFormats.ThreeMf,
                    safeFileName: "fake.3mf"),
                specification,
                CancellationToken.None);

        _ = result.Problems.Select(problem => problem.Code).Should()
            .Contain("model_content_invalid");
    }

    private static CalibrationSpecification ThreeMfSpecification(byte[] package)
    {
        CalibrationGenerationContext context = CalibrationGenerationTestData.Context() with
        {
            ImportedAsset = new CalibrationModelReference(
                CalibrationGenerationTestData.ModelId,
                CalibrationCanonicalJson.ComputeBytesSha256(package),
                CalibrationModelFormats.ThreeMf,
                "cube.3mf",
                package.Length,
                "imported"),
        };

        return CalibrationGenerationTestData.Compiler().Compile(
            context,
            new FinalVerificationCalibrationOptions
            {
                Model3DId = CalibrationGenerationTestData.ModelId,
            }).Value!;
    }
}
