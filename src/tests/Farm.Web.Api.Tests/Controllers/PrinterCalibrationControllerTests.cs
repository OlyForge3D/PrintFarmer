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
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Controllers;

[Collection("SlicerDisabled")]
public sealed class PrinterCalibrationControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/api/printers/calibration-candidates")]
    [InlineData("/api/printers/11111111-1111-1111-1111-111111111111/calibration-context?slicerType=OrcaSlicer")]
    public async Task CalibrationRoute_AnonymousCaller_ReturnsAuthenticationRequired(
        string route)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        HttpResponseMessage response = await client.GetAsync(route);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Theory]
    [InlineData("/api/printers/calibration-candidates")]
    [InlineData("/api/printers/11111111-1111-1111-1111-111111111111/calibration-context?slicerType=OrcaSlicer")]
    public async Task CalibrationRoute_WithoutReadPermission_ReturnsPermissionDenied(
        string route)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");

        HttpResponseMessage response = await client.GetAsync(route);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("permission_denied");
    }

    [Fact]
    public async Task GetContextAsync_WithUnsupportedSlicerType_ReturnsStableBadRequest()
    {
        using HttpClient client = CreateCalibrationReaderClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/calibration-context?slicerType=PrusaSlicer");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("unsupported_slicer_type");
    }

    [Fact]
    public async Task GetContextAsync_WithDisabledOrMissingPrinter_ReturnsHiddenNotFound()
    {
        Guid disabledPrinterId = await SeedEligiblePrinterAsync(isEnabled: false);
        using HttpClient client = CreateCalibrationReaderClient();

        foreach (Guid printerId in new[] { disabledPrinterId, Guid.NewGuid() })
        {
            HttpResponseMessage response = await client.GetAsync(
                $"/api/printers/{printerId}/calibration-context?slicerType=OrcaSlicer");
            string body = await response.Content.ReadAsStringAsync();

            _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
            using JsonDocument document = JsonDocument.Parse(body);
            _ = document.RootElement.GetProperty("code").GetString()
                .Should().Be("printer_not_found");
        }
    }

    [Fact]
    public async Task GetContextAsync_WithChangedConfigurationRevision_ReturnsStableConflict()
    {
        Guid printerId = await SeedEligiblePrinterAsync();
        using HttpClient client = CreateCalibrationReaderClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{printerId}/calibration-context" +
            "?slicerType=OrcaSlicer&configurationRevision=999");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("printer_configuration_changed");
        _ = document.RootElement.GetProperty("currentConfigurationRevision")
            .GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task CalibrationRoutes_InMicroservicesDeployment_KeepCandidatesIndependentOfProfiles()
    {
        await using SlicerDisabledWebApplicationFactory microservicesFactory = new(
            "microservices",
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            });
        using HttpClient client = microservicesFactory.CreateClient();

        HttpResponseMessage candidates =
            await client.GetAsync("/api/printers/calibration-candidates");
        string candidatesBody = await candidates.Content.ReadAsStringAsync();
        _ = candidates.StatusCode.Should().Be(HttpStatusCode.OK, candidatesBody);
        using (JsonDocument document = JsonDocument.Parse(candidatesBody))
        {
            _ = document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        }

        HttpResponseMessage context = await client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/calibration-context?slicerType=OrcaSlicer");
        string contextBody = await context.Content.ReadAsStringAsync();
        _ = context.StatusCode.Should().Be(HttpStatusCode.NotFound, contextBody);
        using (JsonDocument document = JsonDocument.Parse(contextBody))
        {
            _ = document.RootElement.GetProperty("code").GetString()
                .Should().Be("printer_not_found");
        }

        using JsonDocument capabilities =
            await client.GetFromJsonAsync<JsonDocument>("/api/system/capabilities")
            ?? throw new InvalidOperationException("Missing capability response.");
        _ = capabilities.RootElement.GetProperty("deploymentMode").GetString()
            .Should().Be("split");
        _ = capabilities.RootElement.GetProperty("calibrationContextEnabled")
            .GetBoolean().Should().BeFalse();
        _ = capabilities.RootElement.GetProperty("calibration")
            .GetProperty("operational").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetCandidatesAndContext_WithEligiblePrinter_ReturnCredentialFreeContract()
    {
        Guid printerId = await SeedEligiblePrinterAsync();
        Guid subjectId = Guid.NewGuid();
        using HttpClient client = CreateCalibrationReaderClient(subjectId);

        HttpResponseMessage candidatesResponse = await client.GetAsync(
            "/api/printers/calibration-candidates");
        string candidatesBody = await candidatesResponse.Content.ReadAsStringAsync();

        _ = candidatesResponse.StatusCode.Should().Be(HttpStatusCode.OK, candidatesBody);
        using (JsonDocument candidates = JsonDocument.Parse(candidatesBody))
        {
            JsonElement candidate = candidates.RootElement.EnumerateArray().Single();
            _ = candidate.GetProperty("id").GetGuid().Should().Be(printerId);
            _ = candidate.GetProperty("eligible").GetBoolean().Should().BeTrue();
            _ = candidate.GetProperty("firmware").GetProperty("family").GetString()
                .Should().Be("Klipper");
            _ = candidate.GetProperty("slicer").GetProperty("distribution").GetString()
                .Should().Be("upstream");
        }

        HttpResponseMessage contextResponse = await client.GetAsync(
            $"/api/printers/{printerId}/calibration-context?slicerType=OrcaSlicer");
        string contextBody = await contextResponse.Content.ReadAsStringAsync();

        _ = contextResponse.StatusCode.Should().Be(HttpStatusCode.OK, contextBody);
        using JsonDocument context = JsonDocument.Parse(contextBody);
        JsonElement root = context.RootElement;
        _ = root.GetProperty("eligible").GetBoolean().Should().BeTrue();
        _ = root.GetProperty("capturedBySubject").GetString()
            .Should().Be(subjectId.ToString());
        _ = root.GetProperty("snapshotSha256").GetString()
            .Should().MatchRegex("^[0-9a-f]{64}$");
        _ = root.GetProperty("snapshot").GetProperty("profiles")
            .GetProperty("machine").GetProperty("exactJson").GetString()
            .Should().Contain("\"gcode_flavor\":\"klipper\"");
        _ = root.GetProperty("snapshot").GetProperty("physicalSpools")
            .EnumerateArray().Should().ContainSingle();

        string normalized = contextBody.ToLowerInvariant();
        _ = normalized.Should().NotContain("printer-api-key");
        _ = normalized.Should().NotContain("printer-password");
        _ = normalized.Should().NotContain("printer-user");
        _ = normalized.Should().NotContain("10.0.0.42");
        _ = normalized.Should().NotContain("\"serverurl\"");
        _ = normalized.Should().NotContain("\"apikey\"");
        _ = normalized.Should().NotContain("\"password\"");
        _ = normalized.Should().NotContain("\"username\"");
    }

    [Fact]
    public async Task GetContextAsync_WithPrivateProfiles_EnforcesOwnerScopeAndFarmAdminBypass()
    {
        Guid ownerUserId = Guid.NewGuid();
        Guid printerId = await SeedEligiblePrinterAsync(
            profilesPublic: false,
            profileOwnerUserId: ownerUserId);

        using HttpClient nonOwnerClient = CreateCalibrationReaderClient();
        string route =
            $"/api/printers/{printerId}/calibration-context?slicerType=OrcaSlicer";
        HttpResponseMessage nonOwnerResponse = await nonOwnerClient.GetAsync(route);
        string nonOwnerBody = await nonOwnerResponse.Content.ReadAsStringAsync();

        _ = nonOwnerResponse.StatusCode.Should().Be(HttpStatusCode.OK, nonOwnerBody);
        using (JsonDocument context = JsonDocument.Parse(nonOwnerBody))
        {
            _ = context.RootElement.GetProperty("eligible").GetBoolean().Should().BeFalse();
            string[] reasonCodes = context.RootElement.GetProperty("rejectionReasons")
                .EnumerateArray()
                .Select(reason => reason.GetProperty("code").GetString()!)
                .ToArray();
            _ = reasonCodes.Should().Contain(
                "machine_profile_not_found",
                "process_profile_not_found",
                "filament_profile_not_found");
            _ = context.RootElement.GetProperty("snapshot").GetProperty("profiles")
                .TryGetProperty("machine", out _).Should().BeFalse();
        }
        _ = nonOwnerBody.Should().NotContain("Test Machine");
        _ = nonOwnerBody.Should().NotContain("Standard 0.20");
        _ = nonOwnerBody.Should().NotContain("Test PLA");

        using HttpClient ownerClient = CreateCalibrationReaderClient(ownerUserId);
        using JsonDocument ownerContext =
            await ownerClient.GetFromJsonAsync<JsonDocument>(route)
            ?? throw new InvalidOperationException("Missing owner calibration context.");
        _ = ownerContext.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue();

        using HttpClient adminClient = CreateFarmAdminClient();
        using JsonDocument adminContext =
            await adminClient.GetFromJsonAsync<JsonDocument>(route)
            ?? throw new InvalidOperationException("Missing admin calibration context.");
        _ = adminContext.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_WithMissingPrintablePolygonCoordinate_ReturnsBadRequestAndDoesNotPersist()
    {
        Guid printerId = await SeedEligiblePrinterAsync();
        string? originalPrintablePolygonJson = await GetPrintablePolygonJsonAsync(printerId);
        using HttpClient client = CreateFarmAdminClient();
        const string payload =
            """{"printablePolygon":[{"x":0,"y":0},{"x":250},{"x":250,"y":250}]}""";

        HttpResponseMessage response = await PutPrinterJsonAsync(client, printerId, payload);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        _ = (await GetPrintablePolygonJsonAsync(printerId)).Should().Be(originalPrintablePolygonJson);
    }

    [Fact]
    public async Task UpdateAsync_WithMissingExcludedRegionCoordinate_ReturnsBadRequestAndDoesNotPersist()
    {
        Guid printerId = await SeedEligiblePrinterAsync();
        string? originalExcludedRegionsJson = await GetExcludedRegionsJsonAsync(printerId);
        using HttpClient client = CreateFarmAdminClient();
        const string payload =
            """
            {"excludedRegions":[{"name":"unsafe","polygon":[{"x":10,"y":10},{"y":20},{"x":20,"y":20}]}]}
            """;

        HttpResponseMessage response = await PutPrinterJsonAsync(client, printerId, payload);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        _ = (await GetExcludedRegionsJsonAsync(printerId)).Should().Be(originalExcludedRegionsJson);
    }

    [Fact]
    public async Task UpdateAsync_WithCompleteCalibrationGeometry_ReturnsOkAndPersistsGeometry()
    {
        Guid printerId = await SeedEligiblePrinterAsync();
        using HttpClient client = CreateFarmAdminClient();
        const string payload =
            """
            {
              "printablePolygon":[{"x":0,"y":0},{"x":250,"y":0},{"x":250,"y":250}],
              "excludedRegions":[{"name":"unsafe","polygon":[{"x":10,"y":10},{"x":20,"y":10},{"x":20,"y":20}]}]
            }
            """;

        HttpResponseMessage response = await PutPrinterJsonAsync(client, printerId, payload);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        CalibrationPointDto[] printablePolygon =
            JsonSerializer.Deserialize<CalibrationPointDto[]>(await GetPrintablePolygonJsonAsync(printerId) ?? "[]")
            ?? [];
        CalibrationExcludedRegionDto[] excludedRegions =
            JsonSerializer.Deserialize<CalibrationExcludedRegionDto[]>(await GetExcludedRegionsJsonAsync(printerId) ?? "[]")
            ?? [];
        _ = printablePolygon.Should().HaveCount(3);
        _ = excludedRegions.Should().ContainSingle()
            .Which.Polygon.Should().HaveCount(3);
    }

    private HttpClient CreateCalibrationReaderClient(Guid? userId = null)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            (userId ?? Guid.NewGuid()).ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            PrintFarmerPermissions.Calibration.Read);
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

    private async Task<string?> GetPrintablePolygonJsonAsync(Guid printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.FindAsync(printerId)
            ?? throw new InvalidOperationException("Missing seeded printer.");
        return printer.PrintablePolygonJson;
    }

    private async Task<string?> GetExcludedRegionsJsonAsync(Guid printerId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer printer = await db.Printers.FindAsync(printerId)
            ?? throw new InvalidOperationException("Missing seeded printer.");
        return printer.ExcludedRegionsJson;
    }

    private async Task<Guid> SeedEligiblePrinterAsync(
        bool isEnabled = true,
        bool profilesPublic = true,
        Guid? profileOwnerUserId = null)
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
                Name = "Calibration-ready printer",
                ServerUrl = "http://10.0.0.42",
                BackendPort = 7125,
                Backend = (int)PrinterBackend.Moonraker,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                IsEnabled = isEnabled,
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
                CalibrationHardwareVerifiedAtUtc = nowUtc,
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
                IsPublic = profilesPublic,
                CreatedByUserId = profileOwnerUserId,
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
                IsPublic = profilesPublic,
                CreatedByUserId = profileOwnerUserId,
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
                IsPublic = profilesPublic,
                CreatedByUserId = profileOwnerUserId,
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
