using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Security;

public sealed class SensitiveSerializationTests
{
    [Fact]
    public void Serialize_Printer_DoesNotExposeCredentialsOrNetworkSecrets()
    {
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "secret printer",
            ServerUrl = "http://printer.internal:7125",
            OriginalServerUrl = "http://original-printer.internal:7125",
            ApiKey = "printer-api-key",
            Username = "printer-user",
            Password = "printer-password",
            Credential = PrinterCredential.FromAll(
                "transient-api-key",
                "transient-user",
                "transient-password"),
        };
        Camera camera = new()
        {
            Id = Guid.NewGuid(),
            Name = "secret camera",
            StreamUrl = "http://camera.internal/stream",
            SnapshotUrl = "http://camera.internal/snapshot",
            HealthMessage = "camera-password",
        };

        string json = JsonSerializer.Serialize(new { printer, camera });

        AssertSensitiveValuesAreAbsent(
            json,
            "printer.internal",
            "original-printer.internal",
            "printer-api-key",
            "printer-user",
            "printer-password",
            "transient-api-key",
            "transient-user",
            "transient-password",
            "camera.internal",
            "camera-password");
    }

    [Fact]
    public void PrinterComputedUrls_RemoveEmbeddedCredentials()
    {
        Printer printer = new()
        {
            ServerUrl = "http://embedded-user:embedded-password@printer.internal:7125/path",
            BackendPort = 7125,
            FrontendPort = 80,
        };

        printer.BackendUrl.Should().Be("http://printer.internal:7125/path");
        printer.FrontendUrl.Should().Be("http://printer.internal/path");
        printer.BackendUrl.Should().NotContain("embedded-user").And.NotContain("embedded-password");
        printer.FrontendUrl.Should().NotContain("embedded-user").And.NotContain("embedded-password");
    }

    [Fact]
    public void Serialize_SlicerEntities_DoesNotExposeEndpointsKeysInputsOrStoragePaths()
    {
        SliceJob job = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "file:///private/model.stl",
            ModelFileName = "model.stl",
            SlicerProfileJson = "{\"secret\":\"profile\"}",
            RequiredCapabilitiesJson = "[\"private-capability\"]",
            ResultFileUrl = @"D:\private\result.gcode",
            ErrorMessage = "private exception detail",
        };
        Artifact artifact = new()
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            RelativePath = "private/artifact.gcode",
            Sha256 = "private-sha256",
        };
        Worker worker = new()
        {
            Id = Guid.NewGuid(),
            EndpointUrl = "http://worker.internal:8080",
            ApiKey = "worker-api-key",
            CapabilitiesJson = "[\"orcaslicer\"]",
            MetadataJson = "{\"private\":true}",
        };
        SlicerService slicer = new()
        {
            Id = Guid.NewGuid(),
            Name = "orca",
            Host = "http://slicer.internal:8080",
            UiManifestUrl = "http://slicer.internal/manifest",
            ApiKey = "slicer-api-key",
            CapabilitiesJson = "{\"private\":true}",
        };

        string json = JsonSerializer.Serialize(new { job, artifact, worker, slicer });

        AssertSensitiveValuesAreAbsent(
            json,
            "file:///private/model.stl",
            "\"secret\":\"profile\"",
            "private-capability",
            @"D:\private\result.gcode",
            "private exception detail",
            "private/artifact.gcode",
            "private-sha256",
            "worker.internal",
            "worker-api-key",
            "slicer.internal",
            "slicer-api-key");
    }

    [Fact]
    public void Serialize_PrinterResponseDtos_ExposesOnlyExplicitlyEditableSensitiveDetails()
    {
        var recordCases = new Dictionary<Type, IReadOnlyDictionary<string, string>>
        {
            [typeof(CompletePrinterDto)] = new Dictionary<string, string>
            {
                ["ApiKey"] = "complete-api-key",
                ["OriginalServerUrl"] = "http://complete.internal",
                ["ThumbnailUrl"] = "http://complete.internal/private-thumbnail.png",
                ["CameraStreamUrl"] = "http://complete-camera.internal",
                ["BackendUrl"] = "http://complete-backend.internal",
            },
            [typeof(PrinterFastDto)] = new Dictionary<string, string>
            {
                ["ApiKey"] = "fast-api-key",
                ["OriginalServerUrl"] = "http://fast.internal",
                ["CameraStreamUrl"] = "http://fast-camera.internal/stream",
                ["CameraSnapshotUrl"] = "http://fast-camera.internal/snapshot",
                ["BackendUrl"] = "http://fast-backend.internal",
                ["FrontendUrl"] = "http://fast-frontend.internal",
            },
            [typeof(PrinterBasicDto)] = new Dictionary<string, string>
            {
                ["ApiKey"] = "basic-api-key",
                ["OriginalServerUrl"] = "http://basic.internal",
                ["BackendUrl"] = "http://basic-backend.internal",
                ["FrontendUrl"] = "http://basic-frontend.internal",
            },
            [typeof(PrinterDetailsDto)] = new Dictionary<string, string>
            {
                ["ApiKey"] = "details-api-key",
                ["CameraStreamUrl"] = "http://details-camera.internal/stream",
                ["CameraSnapshotUrl"] = "http://details-camera.internal/snapshot",
                ["OriginalServerUrl"] = "http://details-original.internal",
                ["Username"] = "details-user",
            },
        };

        foreach ((Type type, IReadOnlyDictionary<string, string> values) in recordCases)
        {
            object dto = CreateRecord(type, values);
            string json = JsonSerializer.Serialize(dto, type);
            AssertSensitiveValuesAreAbsent(json, values.Values.ToArray());
        }

        var complete = (CompletePrinterDto)CreateRecord(
            typeof(CompletePrinterDto),
            new Dictionary<string, string>
            {
                ["FrontendUrl"] = "http://complete-frontend.internal",
            });
        var details = (PrinterDetailsDto)CreateRecord(
            typeof(PrinterDetailsDto),
            new Dictionary<string, string>
            {
                ["ServerUrl"] = "http://details.internal",
                ["Password"] = "details-password",
            });

        _ = JsonSerializer.Serialize(complete).Should().Contain("http://complete-frontend.internal");
        _ = JsonSerializer.Serialize(details)
            .Should().Contain("http://details.internal")
            .And.Contain("details-password");
    }

    [Fact]
    public void Serialize_DiscoveryExportAndCameraDtos_DoesNotExposeWriteOnlySecrets()
    {
        var discovery = new DiscoveryPrinterInfoDto
        {
            Name = "printer",
            ServerUrl = "http://discovery.internal",
            OriginalServerUrl = "http://discovery-original.internal",
            IpAddress = "10.20.30.40",
            ApiKey = "discovery-api-key",
            Username = "discovery-user",
            Password = "discovery-password",
            CameraStreamUrl = "http://discovery-camera.internal/stream",
            CameraSnapshotUrl = "http://discovery-camera.internal/snapshot",
        };
        var capabilities = new PrinterWithCapabilitiesDto
        {
            Name = "printer",
            ModelName = "model",
            ServerUrl = "http://capabilities.internal",
            IpAddress = "10.20.30.41",
            ApiKey = "capabilities-api-key",
            Username = "capabilities-user",
            Password = "capabilities-password",
        };
        var export = new PrinterExportDto
        {
            Name = "printer",
            ServerUrl = "http://export.internal",
            OriginalServerUrl = "http://export-original.internal",
            ApiKey = "export-api-key",
            Username = "export-user",
            Password = "export-password",
        };
        var camera = new CameraDto
        {
            Name = "camera",
            StreamUrl = "http://camera.internal/stream",
            SnapshotUrl = "http://camera.internal/snapshot",
        };
        var displayCamera = new DisplayCameraDto
        {
            Name = "display camera",
            StreamUrl = "http://display-camera.internal/stream",
            SnapshotUrl = "http://display-camera.internal/snapshot",
            StreamProxyUrl = "/api/cameras/00000000-0000-0000-0000-000000000001/stream",
            SnapshotProxyUrl = "/api/cameras/00000000-0000-0000-0000-000000000001/snapshot",
        };

        string json = JsonSerializer.Serialize(new
        {
            discovery,
            capabilities,
            export,
            camera,
            displayCamera,
        });

        AssertSensitiveValuesAreAbsent(
            json,
            "discovery.internal",
            "discovery-original.internal",
            "10.20.30.40",
            "discovery-api-key",
            "discovery-user",
            "discovery-password",
            "discovery-camera.internal",
            "capabilities.internal",
            "10.20.30.41",
            "capabilities-api-key",
            "capabilities-user",
            "capabilities-password",
            "export.internal",
            "export-original.internal",
            "export-api-key",
            "export-user",
            "export-password",
            "camera.internal",
            "display-camera.internal");
        _ = json.Should().Contain("/api/cameras/00000000-0000-0000-0000-000000000001/stream");
    }

    [Fact]
    public void Deserialize_WriteOnlyDiscoveryAndExportSecrets_RemainsSupported()
    {
        const string json = """
            {
              "name": "printer",
              "serverUrl": "http://printer.internal",
              "originalServerUrl": "http://original.internal",
              "ipAddress": "10.20.30.40",
              "apiKey": "api-key",
              "username": "user",
              "password": "password"
            }
            """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        DiscoveryPrinterInfoDto? discovery = JsonSerializer.Deserialize<DiscoveryPrinterInfoDto>(json, options);
        PrinterExportDto? export = JsonSerializer.Deserialize<PrinterExportDto>(json, options);

        _ = discovery.Should().NotBeNull();
        _ = discovery!.ServerUrl.Should().Be("http://printer.internal");
        _ = discovery.ApiKey.Should().Be("api-key");
        _ = export.Should().NotBeNull();
        _ = export!.ServerUrl.Should().Be("http://printer.internal");
        _ = export.Password.Should().Be("password");
    }

    [Fact]
    public void CameraUrlResult_RejectsAbsoluteCameraTargets()
    {
        Action create = () => _ = new CameraUrlResult(
            "http://camera.internal/stream",
            "http://camera.internal/snapshot");

        _ = create.Should().Throw<ArgumentException>();
    }

    private static object CreateRecord(
        Type type,
        IReadOnlyDictionary<string, string> sensitiveValues)
    {
        System.Reflection.ConstructorInfo constructor = type.GetConstructors().Single();
        object?[] arguments = constructor.GetParameters()
            .Select(parameter =>
            {
                if (parameter.Name is not null &&
                    sensitiveValues.TryGetValue(parameter.Name, out string? sensitiveValue))
                {
                    return sensitiveValue;
                }

                if (parameter.ParameterType == typeof(string))
                {
                    return "safe";
                }

                return parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
            })
            .ToArray();
        return constructor.Invoke(arguments);
    }

    private static void AssertSensitiveValuesAreAbsent(string json, params string[] sensitiveValues)
    {
        foreach (string sensitiveValue in sensitiveValues)
        {
            _ = json.Should().NotContain(sensitiveValue);
        }
    }
}
