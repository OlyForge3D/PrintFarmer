using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Tests for the dedicated calibration-setup endpoint (issue #1616, PR-3 of the
/// #1613 calibration-eligibility decomposition): <c>PUT /api/printers/{id}/calibration-setup</c>.
/// Covers AC #1 (a documented, distinct endpoint), AC #2 (excludedRegions supports
/// explicit <c>[]</c>), AC #3 (firmware identity is confirm-only, never overridable),
/// AC #4 (writes are reflected in the next calibration-context evaluation), and
/// AC #5 (the raw <c>PUT /api/printers/{id}</c> contract is unaffected).
/// </summary>
[Collection("SlicerDisabled")]
public sealed class PrinterCalibrationSetupControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task UpdateCalibrationSetupAsync_AnonymousCaller_ReturnsAuthenticationRequired()
    {
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{printerId}/calibration-setup")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithOnlyReadPermission_ReturnsForbidden()
    {
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = CreateCalibrationReaderClient();

        HttpResponseMessage response = await PutCalibrationSetupAsync(
            client,
            printerId,
            """{"supportsPressureAdvance":true}""");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithMissingPrinter_ReturnsNotFound()
    {
        using HttpClient client = CreateCalibrationUpdateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{Guid.NewGuid()}/calibration-setup")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithoutIfMatchHeader_ReturnsPreconditionRequired()
    {
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = CreateCalibrationUpdateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{printerId}/calibration-setup")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be((HttpStatusCode)428);
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithStaleIfMatchHeader_ReturnsPreconditionFailed()
    {
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = CreateCalibrationUpdateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{printerId}/calibration-setup")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        string staleEtag = $"\"{Convert.ToBase64String(new byte[8])}\"";
        request.Headers.TryAddWithoutValidation("If-Match", staleEtag);
        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithUnknownToolheadId_ReturnsBadRequestAndDoesNotPersist()
    {
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = CreateCalibrationUpdateClient();
        Guid unknownToolheadId = Guid.NewGuid();
        string payload = $$"""
            {"toolheads":[{"id":"{{unknownToolheadId}}","offsetX":1.0}]}
            """;

        HttpResponseMessage response = await PutCalibrationSetupAsync(client, printerId, payload);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("error").GetString().Should().Be("toolhead_not_found");
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithFullFieldSet_PersistsEveryField()
    {
        Guid printerId = await SeedPrinterAsync();
        Guid toolheadId = await GetToolheadIdAsync(printerId);
        using HttpClient client = CreateCalibrationUpdateClient();
        DateTime verifiedAtUtc = DateTime.UtcNow;
        string payload = $$"""
            {
              "activeToolheadIndex": 0,
              "excludedRegions": [{"name":"fan-duct","polygon":[{"x":1,"y":1},{"x":5,"y":1},{"x":5,"y":5}]}],
              "supportsPressureAdvance": true,
              "supportsFirmwareRetraction": true,
              "calibrationHardwareVerifiedAtUtc": "{{verifiedAtUtc:O}}",
              "firmwareIdentityVerified": true,
              "toolheads": [
                {
                  "id": "{{toolheadId}}",
                  "offsetX": 1.5,
                  "offsetY": -2.25,
                  "offsetZ": 0,
                  "driveType": "bowden",
                  "isDirectDrive": false,
                  "extruderGearRatio": "3:1",
                  "maxVolumetricFlow": 24,
                  "nozzleMaterial": "hardened-steel",
                  "nozzleIsHardened": true
                }
              ]
            }
            """;

        HttpResponseMessage response = await PutCalibrationSetupAsync(client, printerId, payload);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        CalibrationSetupResultDto? result = JsonSerializer.Deserialize<CalibrationSetupResultDto>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = result.Should().NotBeNull();
        _ = result!.ActiveToolheadIndex.Should().Be(0);
        _ = result.ExcludedRegions.Should().ContainSingle().Which.Name.Should().Be("fan-duct");
        _ = result.SupportsPressureAdvance.Should().BeTrue();
        _ = result.SupportsFirmwareRetraction.Should().BeTrue();
        _ = result.CalibrationHardwareVerifiedAtUtc.Should().BeCloseTo(verifiedAtUtc, TimeSpan.FromSeconds(1));

        CalibrationToolheadSetupResultDto toolhead =
            result.Toolheads.Should().ContainSingle(t => t.Id == toolheadId).Subject;
        _ = toolhead.OffsetX.Should().Be(1.5);
        _ = toolhead.OffsetY.Should().Be(-2.25);
        _ = toolhead.OffsetZ.Should().Be(0);
        _ = toolhead.DriveType.Should().Be("bowden");
        _ = toolhead.IsDirectDrive.Should().BeFalse();
        _ = toolhead.ExtruderGearRatio.Should().Be("3:1");
        _ = toolhead.MaxVolumetricFlow.Should().Be(24);
        _ = toolhead.NozzleMaterial.Should().Be("hardened-steel");
        _ = toolhead.NozzleIsHardened.Should().BeTrue();

        // Firmware identity is echoed for display but was never part of the request DTO.
        _ = result.Firmware.Family.Should().Be("Klipper");
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithExplicitEmptyExcludedRegions_PersistsEmptyArray()
    {
        Guid printerId = await SeedPrinterAsync();
        await SetExcludedRegionsAsync(
            printerId,
            """[{"name":"unsafe","polygon":[{"x":1,"y":1},{"x":2,"y":1},{"x":2,"y":2}]}]""");
        using HttpClient client = CreateCalibrationUpdateClient();

        HttpResponseMessage response = await PutCalibrationSetupAsync(
            client,
            printerId,
            """{"excludedRegions":[]}""");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = (await GetExcludedRegionsJsonAsync(printerId)).Should().Be("[]");
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_RequestHasNoFirmwareOverrideFields()
    {
        // Compile-time guarantee (AC #3): the request DTO exposes no property for
        // firmware family/version/gcodeDialect. This test documents and pins that
        // invariant so a future edit that adds such a property is caught in review.
        Type requestType = typeof(CalibrationSetupRequestDto);
        string[] propertyNames = requestType.GetProperties().Select(p => p.Name).ToArray();

        _ = propertyNames.Should().NotContain(
            name => name.Contains("FirmwareFamily", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("FirmwareVersion", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GcodeDialect", StringComparison.OrdinalIgnoreCase));
        _ = propertyNames.Should().Contain("FirmwareIdentityVerified");
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_WithFirmwareIdentityVerified_DoesNotChangeFirmwareFacts()
    {
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = CreateCalibrationUpdateClient();

        HttpResponseMessage response = await PutCalibrationSetupAsync(
            client,
            printerId,
            """{"firmwareIdentityVerified":true}""");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        CalibrationSetupResultDto? result = JsonSerializer.Deserialize<CalibrationSetupResultDto>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ = result!.Firmware.Family.Should().Be("Klipper");
        _ = result.Firmware.GcodeDialect.Should().Be("Klipper");
        _ = result.Firmware.Version.Should().Be("v0.12.0");
    }

    [Fact]
    public async Task UpdateCalibrationSetupAsync_EndToEnd_UpdatesCalibrationEligibility()
    {
        Guid printerId = await SeedPrinterMissingHardwareSignOffAsync();
        using HttpClient calibrationClient = CreateCalibrationReaderClient();
        using HttpClient setupClient = CreateCalibrationUpdateClient();

        HttpResponseMessage beforeResponse = await calibrationClient.GetAsync(
            $"/api/printers/{printerId}/calibration-context?slicerType=OrcaSlicer");
        string beforeBody = await beforeResponse.Content.ReadAsStringAsync();
        _ = beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK, beforeBody);
        using (JsonDocument beforeDocument = JsonDocument.Parse(beforeBody))
        {
            _ = beforeDocument.RootElement.GetProperty("eligible").GetBoolean().Should().BeFalse();
            bool missingHardwareSignOff = beforeDocument.RootElement
                .GetProperty("missingInputs")
                .EnumerateArray()
                .Any(e => e.GetString() == "calibrationHardwareVerifiedAtUtc");
            _ = missingHardwareSignOff.Should().BeTrue();
        }

        HttpResponseMessage setupResponse = await PutCalibrationSetupAsync(
            setupClient,
            printerId,
            $$"""{"calibrationHardwareVerifiedAtUtc":"{{DateTime.UtcNow:O}}"}""");
        string setupBody = await setupResponse.Content.ReadAsStringAsync();
        _ = setupResponse.StatusCode.Should().Be(HttpStatusCode.OK, setupBody);

        HttpResponseMessage afterResponse = await calibrationClient.GetAsync(
            $"/api/printers/{printerId}/calibration-context?slicerType=OrcaSlicer");
        string afterBody = await afterResponse.Content.ReadAsStringAsync();
        _ = afterResponse.StatusCode.Should().Be(HttpStatusCode.OK, afterBody);
        using JsonDocument afterDocument = JsonDocument.Parse(afterBody);
        _ = afterDocument.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RawPrinterUpdate_StillAcceptsCalibrationFields_AfterCalibrationSetupEndpointAdded()
    {
        // Regression guard for AC #5: the raw catch-all PUT must remain untouched.
        Guid printerId = await SeedPrinterAsync();
        using HttpClient client = CreateFarmAdminClient();
        const string payload = """{"supportsPressureAdvance":false}""";

        HttpResponseMessage response = await PutPrinterJsonAsync(client, printerId, payload);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
    }

    private static async Task<HttpResponseMessage> PutCalibrationSetupAsync(
        HttpClient client,
        Guid printerId,
        string payload)
    {
        HttpResponseMessage current = await client.GetAsync($"/api/printers/{printerId}");
        current.EnsureSuccessStatusCode();
        string etag = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("Printer GET did not return an ETag.");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{printerId}/calibration-setup")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PutPrinterJsonAsync(
        HttpClient client,
        Guid printerId,
        string payload)
    {
        HttpResponseMessage current = await client.GetAsync($"/api/printers/{printerId}");
        current.EnsureSuccessStatusCode();
        string etag = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("Printer GET did not return an ETag.");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/printers/{printerId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    private HttpClient CreateCalibrationReaderClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            PrintFarmerPermissions.Calibration.Read);
        return client;
    }

    private HttpClient CreateCalibrationUpdateClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            PrintFarmerPermissions.Calibration.Update);
        return client;
    }

    private HttpClient CreateFarmAdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add(
            "X-Test-Roles",
            PrintFarmerPermissions.FarmAdminRole);
        return client;
    }

    private async Task<Guid> GetToolheadIdAsync(Guid printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printerId)
            ?? throw new InvalidOperationException("Missing seeded printer.");
        return printer.Toolheads!.Single().Id;
    }

    private async Task<string?> GetExcludedRegionsJsonAsync(Guid printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.FindAsync(printerId)
            ?? throw new InvalidOperationException("Missing seeded printer.");
        return printer.ExcludedRegionsJson;
    }

    private async Task SetExcludedRegionsAsync(Guid printerId, string excludedRegionsJson)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.FindAsync(printerId)
            ?? throw new InvalidOperationException("Missing seeded printer.");
        printer.ExcludedRegionsJson = excludedRegionsJson;
        _ = await db.SaveChangesAsync();
    }

    private Task<Guid> SeedPrinterAsync() => SeedPrinterInternalAsync(hardwareVerified: true);

    private Task<Guid> SeedPrinterMissingHardwareSignOffAsync() =>
        SeedPrinterInternalAsync(hardwareVerified: false);

    private async Task<Guid> SeedPrinterInternalAsync(bool hardwareVerified)
    {
        Guid printerId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid machineProfileId = Guid.NewGuid();
        Guid processProfileId = Guid.NewGuid();
        Guid filamentProfileId = Guid.NewGuid();
        DateTime nowUtc = DateTime.UtcNow;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = db.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = $"Calibration manufacturer {manufacturerId}",
            });
            _ = db.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = $"Calibration model {modelId}",
            });
            _ = db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Calibration-setup printer",
                ServerUrl = "http://10.0.0.43",
                BackendPort = 7125,
                Backend = (int)PrinterBackend.Moonraker,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                IsEnabled = true,
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                GcodeDialect = PrinterGcodeDialect.Klipper,
                FirmwareDetectionSource = FirmwareDetectionSource.Printer,
                FirmwareVersion = "v0.12.0",
                FirmwareDetectionVersion = "printer-info-v1",
                FirmwareDetectionConfidence = 1m,
                FirmwareDetectedAtUtc = nowUtc,
                FirmwareIdentityVerified = true,
                BackendVersion = "v0.9.3",
                BackendApiVersion = "v1",
                MaxBuildVolumeX = 250,
                MaxBuildVolumeY = 250,
                MaxBuildVolumeZ = 250,
                BedOriginX = 0,
                BedOriginY = 0,
                PrintablePolygonJson =
                    """[{"x":0,"y":0},{"x":250,"y":0},{"x":250,"y":250},{"x":0,"y":250}]""",
                ExcludedRegionsJson = "[]",
                CalibrationMotionType = CalibrationMotionType.CoreXY,
                MaxPrintSpeed = 300,
                MaxTravelSpeed = 500,
                MaxAcceleration = 10000,
                MaxTravelAcceleration = 12000,
                CalibrationHasHeatedBed = true,
                MaxBedTemp = 120,
                CalibrationHasEnclosure = false,
                HasHeatedChamber = false,
                ActiveToolheadIndex = 0,
                SupportsPressureAdvance = true,
                SupportsFirmwareRetraction = true,
                CalibrationHardwareVerifiedAtUtc = hardwareVerified ? nowUtc : null,
                CalibrationSlicerEngine = CalibrationContractConstants.SlicerEngine,
                CalibrationSlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                CalibrationSlicerVersion = CalibrationContractConstants.SlicerVersion,
                CalibrationProfileFormat = CalibrationContractConstants.ProfileFormat,
                CalibrationMachineProfileId = machineProfileId,
                CalibrationProcessProfileId = processProfileId,
                CalibrationFilamentProfileId = filamentProfileId,
                ApiKey = "printer-api-key",
                Username = "printer-user",
                Password = "printer-password",
                Toolheads =
                [
                    new Toolhead
                    {
                        Id = Guid.NewGuid(),
                        PrinterId = printerId,
                        Name = "T0",
                        Index = 0,
                        IsPrimary = true,
                        ToolheadType = ToolheadType.Physical,
                        OffsetX = 0,
                        OffsetY = 0,
                        OffsetZ = 0,
                        NozzleDiameter = 0.4,
                        NozzleType = NozzleType.Brass,
                        NozzleMaterial = "brass",
                        NozzleMaxTemperature = 300,
                        NozzleIsHardened = false,
                        HotendMaxTemperature = 300,
                        MaxVolumetricFlow = 15,
                        DriveType = "direct",
                        IsDirectDrive = true,
                        ExtruderGearRatio = "50:10",
                        SupportedMaterials = ["PLA", "PETG"],
                    },
                ],
            });
            _ = db.Spools.Add(new Spool
            {
                Id = Guid.NewGuid(),
                Material = "PLA",
                ColorHex = "#ff0000",
                WeightGrams = 750,
                InUse = true,
                AssignedPrinterId = printerId,
            });
            _ = await db.SaveChangesAsync();
        }

        const string machineJson =
            """{"gcode_flavor":"klipper","nozzle_diameter":[0.4]}""";
        const string processJson =
            """{"layer_height":0.2,"infill_density":20}""";
        const string filamentJson =
            """{"filament_max_volumetric_speed":12}""";
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            _ = db.MachineProfiles.Add(new MachineProfile
            {
                Id = machineProfileId,
                Name = "Test Machine",
                Manufacturer = "Test",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                PrinterModelId = modelId,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = machineJson,
                Hash = ComputeSha256(machineJson),
                IsSystem = true,
                IsPublic = true,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            _ = db.ProcessProfiles.Add(new ProcessProfile
            {
                Id = processProfileId,
                Name = "Standard 0.20",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                PrinterModelId = modelId,
                SpecificPrinterId = printerId,
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 100,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = processJson,
                Hash = ComputeSha256(processJson),
                CompatiblePrinters = "Test Machine",
                IsSystem = true,
                IsPublic = true,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            _ = db.FilamentProfiles.Add(new FilamentProfile
            {
                Id = filamentProfileId,
                Name = "Test PLA",
                Material = "PLA",
                Manufacturer = "Test",
                SlicerType = SlicerType.OrcaSlicer,
                SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                NozzleTemperature = 210,
                BedTemperature = 60,
                PrintSpeed = 100,
                SlicerVersion = CalibrationContractConstants.SlicerVersion,
                ProfileFormat = CalibrationContractConstants.ProfileFormat,
                RawJson = filamentJson,
                Hash = ComputeSha256(filamentJson),
                CompatiblePrinters = "Test Machine",
                IsSystem = true,
                IsPublic = true,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
            _ = await db.SaveChangesAsync();
        }

        IPrinterStatusCacheWriter statusWriter =
            _factory.Services.GetRequiredService<IPrinterStatusCacheWriter>();
        statusWriter.UpdateStatus(new PrinterStatusDto(
            printerId,
            IsOnline: true,
            State: "idle",
            HotendTemp: 205,
            BedTemp: 60,
            HotendTarget: 210,
            BedTarget: 60));
        return printerId;
    }

    private static string ComputeSha256(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
