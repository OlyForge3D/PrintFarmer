using System.IO.Compression;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Slicer.Module.Tests.Services;

public class ThreeMfMetadataServiceTests
{
    private readonly ThreeMfMetadataService _sut = new(NullLogger<ThreeMfMetadataService>.Instance);

    private static MemoryStream CreateThreeMfArchive(string modelXml)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("3D/3dmodel.model");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(modelXml);
        }

        ms.Position = 0;
        return ms;
    }

    private const string FullModelXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
          <metadata name="Title">Test Model</metadata>
          <metadata name="Designer">Jane Doe</metadata>
          <metadata name="Description">A test 3MF model</metadata>
          <metadata name="Application">OrcaSlicer</metadata>
          <metadata name="CreationDate">2024-01-15</metadata>
          <metadata name="ModificationDate">2024-06-20</metadata>
          <resources>
            <basematerials id="1">
              <base name="PLA" displaycolor="#FF0000"/>
              <base name="ABS" displaycolor="#00FF00"/>
            </basematerials>
          </resources>
        </model>
        """;

    [Fact]
    public async Task ExtractMetadataAsync_ValidThreeMf_ExtractsAllFields()
    {
        using MemoryStream stream = CreateThreeMfArchive(FullModelXml);

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(stream, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Test Model");
        result.Designer.Should().Be("Jane Doe");
        result.Description.Should().Be("A test 3MF model");
        result.Application.Should().Be("OrcaSlicer");
        result.CreationDate.Should().Be("2024-01-15");
        result.ModificationDate.Should().Be("2024-06-20");
    }

    [Fact]
    public async Task ExtractMetadataAsync_WithMaterials_ExtractsMaterialNames()
    {
        using MemoryStream stream = CreateThreeMfArchive(FullModelXml);

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(stream, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Materials.Should().HaveCount(2);
        result.Materials.Should().Contain("PLA");
        result.Materials.Should().Contain("ABS");
    }

    [Fact]
    public async Task ExtractMetadataAsync_GeneratesAutoTags()
    {
        using MemoryStream stream = CreateThreeMfArchive(FullModelXml);

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(stream, CancellationToken.None);

        result.Should().NotBeNull();
        result!.AutoTags.Should().Contain("designer:Jane Doe");
        result.AutoTags.Should().Contain("material:PLA");
        result.AutoTags.Should().Contain("material:ABS");
        result.AutoTags.Should().Contain("app:OrcaSlicer");
    }

    [Fact]
    public async Task ExtractMetadataAsync_MissingModelEntry_ReturnsNull()
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("some-other-file.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("not a model");
        }

        ms.Position = 0;

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(ms, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractMetadataAsync_EmptyMetadata_ReturnsEmptyFields()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
              <resources />
            </model>
            """;

        using MemoryStream stream = CreateThreeMfArchive(xml);

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(stream, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Title.Should().BeNull();
        result.Designer.Should().BeNull();
        result.Description.Should().BeNull();
        result.Application.Should().BeNull();
        result.Materials.Should().BeEmpty();
        result.AutoTags.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractMetadataAsync_InvalidZip_ReturnsNull()
    {
        using var ms = new MemoryStream("this is not a zip file"u8.ToArray());

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(ms, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractMetadataAsync_DuplicateMaterials_DeduplicatesByName()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
              <resources>
                <basematerials id="1">
                  <base name="PLA" displaycolor="#FF0000"/>
                  <base name="pla" displaycolor="#00FF00"/>
                </basematerials>
              </resources>
            </model>
            """;

        using MemoryStream stream = CreateThreeMfArchive(xml);

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(stream, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Materials.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExtractMetadataAsync_CreatorMapsToDesigner()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
              <metadata name="Creator">John Smith</metadata>
            </model>
            """;

        using MemoryStream stream = CreateThreeMfArchive(xml);

        ThreeMfMetadataDto? result = await _sut.ExtractMetadataAsync(stream, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Designer.Should().Be("John Smith");
        result.AutoTags.Should().Contain("designer:John Smith");
    }
}
