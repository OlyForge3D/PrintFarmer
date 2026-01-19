using Farm.Infrastructure;
using Farm.Web.Api.Controllers.Requests;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Farm.Web.Api.Tests.Controllers.Requests;

public class CreateManufacturerRequestTests
{
    [Fact]
    public void Constructor_WithValidName_Succeeds()
    {
        var request = new CreateManufacturerRequest(Name: "Prusa");

        request.Name.Should().Be("Prusa");
    }

    [Fact]
    public void Constructor_WithDifferentNames_Succeeds()
    {
        string[] names = new[] { "Prusa", "Ultimaker", "Creality", "Anycubic", "3D Systems" };

        foreach (string? name in names)
        {
            var request = new CreateManufacturerRequest(Name: name);
            request.Name.Should().Be(name);
        }
    }

    [Fact]
    public void CreateManufacturerRequest_IsRecord()
    {
        var request1 = new CreateManufacturerRequest("Prusa");
        var request2 = new CreateManufacturerRequest("Prusa");

        request1.Equals(request2).Should().BeTrue();
    }

    [Fact]
    public void CreateManufacturerRequest_DifferentNames_AreNotEqual()
    {
        var request1 = new CreateManufacturerRequest("Prusa");
        var request2 = new CreateManufacturerRequest("Ultimaker");

        request1.Equals(request2).Should().BeFalse();
    }

    [Fact]
    public void CreateManufacturerRequest_CanBeDeconstructed()
    {
        var request = new CreateManufacturerRequest("Prusa");

        string name = request.Name;

        name.Should().Be("Prusa");
    }

    [Fact]
    public void CreateManufacturerRequest_WithCaseSensitiveNames()
    {
        var request1 = new CreateManufacturerRequest("prusa");
        var request2 = new CreateManufacturerRequest("PRUSA");
        var request3 = new CreateManufacturerRequest("Prusa");

        request1.Name.Should().Be("prusa");
        request2.Name.Should().Be("PRUSA");
        request3.Name.Should().Be("Prusa");
    }

    [Fact]
    public void CreateManufacturerRequest_WithSpecialCharacters()
    {
        var request = new CreateManufacturerRequest("3D Systems");

        request.Name.Should().Be("3D Systems");
    }

    [Fact]
    public void CreateManufacturerRequest_WithLongName()
    {
        string longName = "Very Long Manufacturer Name With Many Words";

        var request = new CreateManufacturerRequest(longName);

        request.Name.Should().Be(longName);
    }

    [Fact]
    public void CreateManufacturerRequest_ToString_IncludesName()
    {
        var request = new CreateManufacturerRequest("Prusa");

        request.ToString().Should().Contain("Prusa");
    }

    [Fact]
    public void CreateManufacturerRequest_GetHashCode_ConsistentForSameValues()
    {
        var request1 = new CreateManufacturerRequest("Prusa");
        var request2 = new CreateManufacturerRequest("Prusa");

        request1.GetHashCode().Should().Be(request2.GetHashCode());
    }
}

public class DiscoveryStreamRequestTests
{
    [Fact]
    public void Constructor_WithDefaults_Succeeds()
    {
        var request = new DiscoveryStreamRequest();

        request.Backends.Should().BeNull();
        request.AutoRegister.Should().BeFalse();
    }

    [Fact]
    public void Backends_CanBeSet()
    {
        var request = new DiscoveryStreamRequest
        {
            Backends = new[] { PrinterBackend.Moonraker }
        };

        request.Backends.Should().NotBeNull();
        request.Backends.Should().HaveCount(1);
    }

    [Fact]
    public void AutoRegister_CanBeSet()
    {
        var request = new DiscoveryStreamRequest
        {
            AutoRegister = true
        };

        request.AutoRegister.Should().BeTrue();
    }

    [Fact]
    public void DiscoveryStreamRequest_WithMultipleBackends()
    {
        var request = new DiscoveryStreamRequest
        {
            Backends = new[] { PrinterBackend.Moonraker, PrinterBackend.PrusaLink }
        };

        request.Backends.Should().HaveCount(2);
        request.Backends.Should().Contain(PrinterBackend.Moonraker);
        request.Backends.Should().Contain(PrinterBackend.PrusaLink);
    }

    [Fact]
    public void DiscoveryStreamRequest_WithAllBackends()
    {
        PrinterBackend[] allBackends = new[]
        {
            PrinterBackend.Moonraker,
            PrinterBackend.PrusaLink,
            PrinterBackend.OctoPrint,
            PrinterBackend.SDCP
        };

        var request = new DiscoveryStreamRequest
        {
            Backends = allBackends
        };

        request.Backends.Should().Equal(allBackends);
    }

    [Fact]
    public void DiscoveryStreamRequest_CanBeModified()
    {
        var request = new DiscoveryStreamRequest();

        request.AutoRegister = true;
        request.Backends = new[] { PrinterBackend.OctoPrint };

        request.AutoRegister.Should().BeTrue();
        request.Backends.Should().HaveCount(1);
    }

    [Fact]
    public void DiscoveryStreamRequest_WithNullBackends()
    {
        var request = new DiscoveryStreamRequest { Backends = null };

        request.Backends.Should().BeNull();
    }

    [Fact]
    public void DiscoveryStreamRequest_WithEmptyBackends()
    {
        var request = new DiscoveryStreamRequest { Backends = Array.Empty<PrinterBackend>() };

        request.Backends.Should().HaveCount(0);
    }
}

public class FileOperationRequestTests
{
    [Fact]
    public void Constructor_WithDefaults_Succeeds()
    {
        var request = new FileOperationRequest();

        request.FileName.Should().Be(string.Empty);
    }

    [Fact]
    public void FileName_CanBeSet()
    {
        var request = new FileOperationRequest
        {
            FileName = "test.gcode"
        };

        request.FileName.Should().Be("test.gcode");
    }

    [Fact]
    public void FileName_WithSpecialCharacters()
    {
        var request = new FileOperationRequest
        {
            FileName = "test-file (1).gcode"
        };

        request.FileName.Should().Be("test-file (1).gcode");
    }

    [Fact]
    public void FileName_WithPath()
    {
        var request = new FileOperationRequest
        {
            FileName = "folder/subfolder/file.gcode"
        };

        request.FileName.Should().Be("folder/subfolder/file.gcode");
    }

    [Fact]
    public void FileName_WithUnicodeCharacters()
    {
        var request = new FileOperationRequest
        {
            FileName = "测试_test_файл.gcode"
        };

        request.FileName.Should().Be("测试_test_файл.gcode");
    }

    [Fact]
    public void FileName_CanBeMultipleTimesSet()
    {
        var request = new FileOperationRequest();

        request.FileName = "file1.gcode";
        request.FileName.Should().Be("file1.gcode");

        request.FileName = "file2.gcode";
        request.FileName.Should().Be("file2.gcode");
    }

    [Fact]
    public void FileName_WithLongPath()
    {
        string longPath = string.Join("/", Enumerable.Range(0, 20).Select(i => $"folder{i}")) + "/file.gcode";

        var request = new FileOperationRequest { FileName = longPath };

        request.FileName.Should().Be(longPath);
    }

    [Fact]
    public void FileOperationRequest_ToString_ReturnsTypeName()
    {
        var request = new FileOperationRequest { FileName = "test.gcode" };

        request.ToString().Should().Be("Farm.Web.Api.Controllers.Requests.FileOperationRequest");
    }
}

public class UpdateModelRequestTests
{
    [Fact]
    public void Constructor_WithMinimalData_Succeeds()
    {
        var request = new UpdateModelRequest(
            Name: "Prusa CORE One",
            Type: null,
            MaxX: null,
            MaxY: null,
            MaxZ: null,
            DefaultBackend: null,
            SupportedFilamentTypeIds: null);

        request.Name.Should().Be("Prusa CORE One");
    }

    [Fact]
    public void Constructor_WithAllParameters_Succeeds()
    {
        Guid[] filamentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var request = new UpdateModelRequest(
            Name: "Prusa CORE One",
            Type: MotionType.Cartesian,
            MaxX: 200,
            MaxY: 200,
            MaxZ: 200,
            DefaultBackend: PrinterBackend.Moonraker,
            SupportedFilamentTypeIds: filamentIds,
            HasHeatedBed: true,
            HasEnclosure: false,
            MultiMaterial: false,
            NumberOfExtruders: 1,
            SupportsAutoLeveling: true,
            MaxBedTemp: 120,
            MaxPrintSpeed: 200);

        request.Name.Should().Be("Prusa CORE One");
        request.Type.Should().Be(MotionType.Cartesian);
        request.MaxX.Should().Be(200);
        request.MaxY.Should().Be(200);
        request.MaxZ.Should().Be(200);
        request.DefaultBackend.Should().Be(PrinterBackend.Moonraker);
        request.SupportedFilamentTypeIds.Should().HaveCount(2);
        request.HasHeatedBed.Should().BeTrue();
        request.HasEnclosure.Should().BeFalse();
        request.NumberOfExtruders.Should().Be(1);
        request.MaxBedTemp.Should().Be(120);
        request.MaxPrintSpeed.Should().Be(200);
    }

    [Fact]
    public void UpdateModelRequest_WithDifferentBackends()
    {
        PrinterBackend[] backends = new[] { PrinterBackend.Moonraker, PrinterBackend.PrusaLink, PrinterBackend.OctoPrint };

        foreach (PrinterBackend backend in backends)
        {
            var request = new UpdateModelRequest("Model", null, null, null, null, backend, null);
            request.DefaultBackend.Should().Be(backend);
        }
    }

    [Fact]
    public void UpdateModelRequest_WithMotionTypes()
    {
        MotionType[] motionTypes = new[] { MotionType.Cartesian, MotionType.Delta, MotionType.Unknown };

        foreach (MotionType motionType in motionTypes)
        {
            var request = new UpdateModelRequest("Model", motionType, null, null, null, null, null);
            request.Type.Should().Be(motionType);
        }
    }

    [Fact]
    public void UpdateModelRequest_WithBuildVolumeDimensions()
    {
        var request = new UpdateModelRequest(
            "Model",
            null,
            MaxX: 300,
            MaxY: 300,
            MaxZ: 400,
            null,
            null);

        request.MaxX.Should().Be(300);
        request.MaxY.Should().Be(300);
        request.MaxZ.Should().Be(400);
    }

    [Fact]
    public void UpdateModelRequest_WithTemperatureRanges()
    {
        var request = new UpdateModelRequest(
            "Model",
            null, null, null, null, null, null,
            HasHeatedBed: null,
            HasEnclosure: null,
            MultiMaterial: null,
            NumberOfExtruders: null,
            SupportsAutoLeveling: null,
            MaxBedTemp: 140,
            MaxPrintSpeed: 250);

        request.MaxBedTemp.Should().Be(140);
    }

    [Fact]
    public void UpdateModelRequest_IsRecord_WithValueEquality()
    {
        var request1 = new UpdateModelRequest("Prusa", null, null, null, null, null, null);
        var request2 = new UpdateModelRequest("Prusa", null, null, null, null, null, null);

        request1.Equals(request2).Should().BeTrue();
    }

    [Fact]
    public void UpdateModelRequest_DifferentNames_AreNotEqual()
    {
        var request1 = new UpdateModelRequest("Prusa", null, null, null, null, null, null);
        var request2 = new UpdateModelRequest("Ultimaker", null, null, null, null, null, null);

        request1.Equals(request2).Should().BeFalse();
    }

    [Fact]
    public void UpdateModelRequest_WithLongModelName()
    {
        string longName = "Very Long Printer Model Name With Many Words And Characters";
        var request = new UpdateModelRequest(longName, null, null, null, null, null, null);

        request.Name.Should().Be(longName);
    }

    [Fact]
    public void UpdateModelRequest_WithSpecialCharactersInName()
    {
        var request = new UpdateModelRequest("Prusa CORE One (Enhanced)", null, null, null, null, null, null);

        request.Name.Should().Be("Prusa CORE One (Enhanced)");
    }

    [Fact]
    public void UpdateModelRequest_WithZeroTemperatures()
    {
        var request = new UpdateModelRequest(
            "Model",
            null, null, null, null, null, null,
            HasHeatedBed: null,
            HasEnclosure: null,
            MultiMaterial: null,
            NumberOfExtruders: null,
            SupportsAutoLeveling: null,
            MaxBedTemp: 0,
            MaxPrintSpeed: 0);

        request.MaxBedTemp.Should().Be(0);
        request.MaxPrintSpeed.Should().Be(0);
    }

    [Fact]
    public void UpdateModelRequest_WithMultipleFilamentTypes()
    {
        Guid[] filamentIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var request = new UpdateModelRequest(
            "Model",
            null, null, null, null, null,
            SupportedFilamentTypeIds: filamentIds);

        request.SupportedFilamentTypeIds.Should().HaveCount(5);
    }
}

