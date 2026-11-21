using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class OrcaProjectBedSettingsTests
{
    [Fact]
    public void Read_SeparatesGlobalAndPerPlateSelections()
    {
        string path = CreateProject(
            """{"curr_bed_type":"High Temp Plate"}""",
            """
            <config>
              <plate><metadata key="plater_id" value="1"/><metadata key="bed_type" value="Cool Plate"/></plate>
              <plate><metadata key="plater_id" value="2"/><metadata key="bed_type" value="Textured PEI Plate"/></plate>
            </config>
            """);
        try
        {
            OrcaProjectBedSettings settings = OrcaProjectBedSettings.Read(path);
            _ = settings.Global.Should().Be("High Temp Plate");
            _ = settings.Plates.Should().BeEquivalentTo(new Dictionary<int, string>
            {
                [1] = "Cool Plate",
                [2] = "Textured PEI Plate",
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_NoSelections_ReturnsNoChoices()
    {
        string path = CreateProject("{}", """<config><plate><metadata key="plater_id" value="1"/></plate></config>""");
        try
        {
            OrcaProjectBedSettings settings = OrcaProjectBedSettings.Read(path);
            _ = settings.Global.Should().BeNull();
            _ = settings.Plates.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_PrioritizesExplicitThenPlateThenProjectThenHotPlate()
    {
        var settings = new OrcaProjectBedSettings("Cool Plate", new Dictionary<int, string>
        {
            [1] = "Textured PEI Plate",
        });

        _ = settings.Resolve(1, "Engineering Plate").Should().Be("Engineering Plate");
        _ = settings.Resolve(1, null).Should().Be("Textured PEI Plate");
        _ = settings.Resolve(2, null).Should().Be("Cool Plate");
        _ = new OrcaProjectBedSettings(null, new Dictionary<int, string>()).Resolve(1, null)
            .Should().Be("High Temp Plate");
        _ = ((Action)(() => settings.Resolve(1, "unknown"))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ApplyExplicitSelection_ChangesAllPlateOverridesWithoutChangingSource()
    {
        string source = CreateProject(
            """{"curr_bed_type":"High Temp Plate"}""",
            """
            <config>
              <plate><metadata key="plater_id" value="1"/><metadata key="bed_type" value="Cool Plate"/></plate>
              <plate><metadata key="plater_id" value="2"/><metadata key="bed_type" value="Textured PEI Plate"/></plate>
            </config>
            """);
        string destination = Path.Join(Path.GetTempPath(), $"orca-project-bed-copy-{Guid.NewGuid():N}.3mf");
        try
        {
            OrcaProjectBedSettings original = OrcaProjectBedSettings.Read(source);
            _ = original.ApplyExplicitSelection(source, destination, "Engineering Plate").Should().Be(destination);
            _ = OrcaProjectBedSettings.Read(destination).Plates.Values.Should().OnlyContain(value => value == "Engineering Plate");
            _ = OrcaProjectBedSettings.Read(source).Plates.Should().BeEquivalentTo(original.Plates);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Theory]
    [InlineData(null, null, "High Temp Plate")]
    [InlineData("Cool Plate", null, "Cool Plate")]
    [InlineData("Cool Plate", "Engineering Plate", "Engineering Plate")]
    public async Task PrepareAsync_ResolvesBedWithoutModifyingSourceProfiles(
        string? global, string? explicitBedType, string expectedBed)
    {
        string project = CreateProject(
            global is null ? "{}" : $$"""{"curr_bed_type":"{{global}}"}""",
            """<config><plate><metadata key="plater_id" value="1"/><metadata key="bed_type" value="Textured PEI Plate"/></plate></config>""");
        string directory = Path.Join(Path.GetTempPath(), $"orca-bed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string process = Path.Join(directory, "original.json");
        const string original = """{"curr_bed_type":"Cool Plate","layer_height":"0.2"}""";
        await File.WriteAllTextAsync(process, original);
        try
        {
            var prepared = await OrcaProjectBedSettings.PrepareAsync(
                [project], process, directory, explicitBedType, CancellationToken.None);
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(prepared.ProcessPath));
            _ = result.RootElement.GetProperty("curr_bed_type").GetString().Should().Be(expectedBed);
            _ = result.RootElement.GetProperty("layer_height").GetString().Should().Be("0.2");
            _ = (await File.ReadAllTextAsync(process)).Should().Be(original);
            _ = OrcaProjectBedSettings.Read(project).Resolve(1, null).Should().Be("Textured PEI Plate");
            _ = OrcaProjectBedSettings.Read(prepared.ModelPaths[0]).Resolve(1, null)
                .Should().Be(explicitBedType ?? "Textured PEI Plate");
        }
        finally
        {
            File.Delete(project);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_MultipleInputsContainingProject_Fails()
    {
        Func<Task> prepare = async () => await OrcaProjectBedSettings.PrepareAsync(
            ["project.3mf", "model.stl"], "process.json", ".", null, CancellationToken.None);
        _ = await prepare.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(null, "High Temp Plate")]
    [InlineData("Cool Plate", "Cool Plate")]
    public async Task PrepareAsync_WithoutProject_UsesExplicitOrHotPlate(string? explicitBedType, string expectedBed)
    {
        string directory = Path.Join(Path.GetTempPath(), $"orca-bed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string process = Path.Join(directory, "original.json");
        await File.WriteAllTextAsync(process, """{"curr_bed_type":"Cool Plate"}""");
        try
        {
            var prepared = await OrcaProjectBedSettings.PrepareAsync(
                ["model.stl"], process, directory, explicitBedType, CancellationToken.None);
            using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(prepared.ProcessPath));
            _ = result.RootElement.GetProperty("curr_bed_type").GetString().Should().Be(expectedBed);
            _ = prepared.ModelPaths.Should().Equal("model.stl");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareAsync_DuplicateProcessBedType_Fails()
    {
        string directory = Path.Join(Path.GetTempPath(), $"orca-bed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string process = Path.Join(directory, "original.json");
        await File.WriteAllTextAsync(process, """{"curr_bed_type":"Cool Plate","curr_bed_type":"High Temp Plate"}""");
        try
        {
            Func<Task> prepare = async () => await OrcaProjectBedSettings.PrepareAsync(
                ["model.stl"], process, directory, null, CancellationToken.None);
            _ = await prepare.Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [Theory]
    [InlineData("""{"curr_bed_type":"Cool Plate","curr_bed_type":"High Temp Plate"}""", "<config/>")]
    [InlineData("""{"curr_bed_type":"unknown"}""", "<config/>")]
    [InlineData("{}", """<config><plate><metadata key="bed_type" value="Cool Plate"/></plate></config>""")]
    [InlineData("{}", """<config><plate><metadata key="plater_id" value="1"/><metadata key="bed_type" value="Cool Plate"/><metadata key="bed_type" value="High Temp Plate"/></plate></config>""")]
    [InlineData("{}", """<!DOCTYPE config [<!ENTITY x "x">]><config>&x;</config>""")]
    public void Read_AmbiguousOrUnsafeMetadata_Fails(string project, string model)
    {
        string path = CreateProject(project, model);
        try
        {
            Action read = () => OrcaProjectBedSettings.Read(path);
            _ = read.Should().Throw<Exception>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_DuplicateMetadataEntries_Fails()
    {
        string path = CreateProject("{}", "<config/>");
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                WriteEntry(archive, "metadata/PROJECT_SETTINGS.config", "{}");
            }

            Action read = () => OrcaProjectBedSettings.Read(path);
            _ = read.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_InvalidArchiveOrNon3MfZip_Fails(bool zip)
    {
        string path = Path.Join(Path.GetTempPath(), $"orca-project-bed-{Guid.NewGuid():N}.3mf");
        try
        {
            if (zip)
            {
                using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
                WriteEntry(archive, "unrelated.txt", "not a model");
            }
            else
            {
                File.WriteAllText(path, "not a zip archive");
            }

            Action read = () => OrcaProjectBedSettings.Read(path);
            _ = read.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateProject(string project, string model)
    {
        string path = Path.Join(Path.GetTempPath(), $"orca-project-bed-{Guid.NewGuid():N}.3mf");
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Target="/3D/3dmodel.model" Id="rel-1" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/>
            </Relationships>
            """);
        WriteEntry(archive, "3D/3dmodel.model", """<model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02"/>""");
        WriteEntry(archive, "Metadata/project_settings.config", project);
        WriteEntry(archive, "Metadata/model_settings.config", model);
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using Stream stream = archive.CreateEntry(name).Open();
        using StreamWriter writer = new(stream, Encoding.UTF8);
        writer.Write(content);
    }
}
