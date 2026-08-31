using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PartsInventory;
using Farm.Infrastructure.Repositories.PartsInventory;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Canonical inventory wire payloads consumed by the iOS app. Every fixture is captured from
/// an authenticated request through the production controllers and registered MVC serializer.
/// Deterministic service/repository doubles select the response state without duplicating JSON.
/// </summary>
public sealed class InventoryContractTests : IAsyncLifetime
{
    private readonly InventoryContractState _state = new();

    private InventoryContractFactory Factory { get; }

    public InventoryContractTests()
    {
        Factory = new InventoryContractFactory(_state);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Factory.DisposeAsync();

    /// <summary>Empty collection and paged-result variants for every Spoolman inventory list consumed by iOS.</summary>
    [Fact]
    public async Task GetSpoolmanInventoryEndpoints_EmptyCollections_MatchCorpusAsync()
    {
        using HttpClient client = await Factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-spoolman-empty",
            email: "wire-contract-spoolman-empty@example.com");

        await CaptureGetAsync(
            client,
            "/api/spoolman/spools?limit=50&offset=0",
            "inventory/spoolman-spools.empty-collection.json",
            "GET /api/spoolman/spools",
            nameof(GetSpoolmanInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyPagedResult);
        await CaptureGetAsync(
            client,
            "/api/spoolman/filaments",
            "inventory/spoolman-filaments.empty-collection.json",
            "GET /api/spoolman/filaments",
            nameof(GetSpoolmanInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyPagedResult);
        await CaptureGetAsync(
            client,
            "/api/spoolman/vendors",
            "inventory/spoolman-vendors.empty-collection.json",
            "GET /api/spoolman/vendors",
            nameof(GetSpoolmanInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
        await CaptureGetAsync(
            client,
            "/api/spoolman/materials",
            "inventory/spoolman-materials.empty-collection.json",
            "GET /api/spoolman/materials",
            nameof(GetSpoolmanInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
        await CaptureGetAsync(
            client,
            "/api/spoolman/materials/available",
            "inventory/spoolman-available-materials.empty-collection.json",
            "GET /api/spoolman/materials/available",
            nameof(GetSpoolmanInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
    }

    /// <summary>Populated variants for every Spoolman inventory response model consumed by iOS.</summary>
    [Fact]
    public async Task GetSpoolmanInventoryEndpoints_PopulatedCollections_MatchCorpusAsync()
    {
        _state.PopulateSpoolman();
        using HttpClient client = await Factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-spoolman-populated",
            email: "wire-contract-spoolman-populated@example.com");

        await CaptureGetAsync(
            client,
            "/api/spoolman/spools?limit=50&offset=0",
            "inventory/spoolman-spools.populated.json",
            "GET /api/spoolman/spools",
            nameof(GetSpoolmanInventoryEndpoints_PopulatedCollections_MatchCorpusAsync),
            root =>
            {
                JsonElement items = JsonContractAssertions.AssertNonEmptyCollection(root, "items");
                Assert.Equal(1, JsonContractAssertions.AssertProperty(root, "totalCount", JsonValueKind.Number).GetInt32());
                JsonElement spool = items[0];
                Assert.Equal("Wire Contract PLA Spool", JsonContractAssertions.AssertProperty(spool, "name", JsonValueKind.String).GetString());
                Assert.Equal(77, JsonContractAssertions.AssertProperty(spool, "filamentId", JsonValueKind.Number).GetInt32());
                _ = JsonContractAssertions.AssertProperty(spool, "usedPercent", JsonValueKind.Number);
                _ = JsonContractAssertions.AssertProperty(spool, "remainingPercent", JsonValueKind.Number);
                JsonContractAssertions.AssertMissingKey(spool, "hasNfcTag");
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/filaments",
            "inventory/spoolman-filaments.populated.json",
            "GET /api/spoolman/filaments",
            nameof(GetSpoolmanInventoryEndpoints_PopulatedCollections_MatchCorpusAsync),
            root =>
            {
                JsonElement items = JsonContractAssertions.AssertNonEmptyCollection(root, "items");
                Assert.Equal(1, JsonContractAssertions.AssertProperty(root, "totalCount", JsonValueKind.Number).GetInt32());
                JsonElement filament = items[0];
                Assert.Equal("Wire Contract PLA", JsonContractAssertions.AssertProperty(filament, "name", JsonValueKind.String).GetString());
                Assert.Equal("00012345678905", JsonContractAssertions.AssertProperty(filament, "gtin", JsonValueKind.String).GetString());
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/vendors",
            "inventory/spoolman-vendors.populated.json",
            "GET /api/spoolman/vendors",
            nameof(GetSpoolmanInventoryEndpoints_PopulatedCollections_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(1, root.GetArrayLength());
                Assert.Equal("Wire Contract Vendor", JsonContractAssertions.AssertProperty(root[0], "name", JsonValueKind.String).GetString());
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/materials",
            "inventory/spoolman-materials.populated.json",
            "GET /api/spoolman/materials",
            nameof(GetSpoolmanInventoryEndpoints_PopulatedCollections_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(1, root.GetArrayLength());
                Assert.Equal("PLA", JsonContractAssertions.AssertProperty(root[0], "name", JsonValueKind.String).GetString());
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/materials/available",
            "inventory/spoolman-available-materials.populated.json",
            "GET /api/spoolman/materials/available",
            nameof(GetSpoolmanInventoryEndpoints_PopulatedCollections_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(JsonValueKind.Array, root.ValueKind);
                Assert.Equal(["ASA", "PLA"], root.EnumerateArray().Select(value => value.GetString()!).ToArray());
            });
    }

    /// <summary>Missing-key variants for nullable Spoolman inventory fields consumed by iOS.</summary>
    [Fact]
    public async Task GetSpoolmanInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync()
    {
        _state.PopulateSpoolmanMissingKeys();
        using HttpClient client = await Factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-spoolman-missing-keys",
            email: "wire-contract-spoolman-missing-keys@example.com");

        await CaptureGetAsync(
            client,
            "/api/spoolman/spools?limit=50&offset=0",
            "inventory/spoolman-spools.missing-key.json",
            "GET /api/spoolman/spools",
            nameof(GetSpoolmanInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                JsonElement spool = JsonContractAssertions.AssertNonEmptyCollection(root, "items")[0];
                Assert.Equal("Minimal Spool", JsonContractAssertions.AssertProperty(spool, "name", JsonValueKind.String).GetString());
                AssertMissingKeys(
                    spool,
                    "remainingWeightG",
                    "colorHex",
                    "filamentName",
                    "vendor",
                    "registeredAt",
                    "firstUsedAt",
                    "lastUsedAt",
                    "initialWeightG",
                    "usedWeightG",
                    "spoolWeightG",
                    "remainingLengthMm",
                    "usedLengthMm",
                    "location",
                    "lotNumber",
                    "archived",
                    "price",
                    "comment",
                    "filamentId",
                    "usedPercent",
                    "remainingPercent",
                    "hasNfcTag");
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/filaments",
            "inventory/spoolman-filaments.missing-key.json",
            "GET /api/spoolman/filaments",
            nameof(GetSpoolmanInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                JsonElement filament = JsonContractAssertions.AssertNonEmptyCollection(root, "items")[0];
                Assert.Equal(78, JsonContractAssertions.AssertProperty(filament, "id", JsonValueKind.Number).GetInt32());
                AssertMissingKeys(
                    filament,
                    "name",
                    "material",
                    "colorHex",
                    "vendor",
                    "density",
                    "diameter",
                    "weight",
                    "spoolWeight",
                    "price",
                    "settingsExtruderTemp",
                    "settingsBedTemp",
                    "articleNumber",
                    "comment",
                    "multiColorHexes",
                    "externalId",
                    "gtin");
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/vendors",
            "inventory/spoolman-vendors.missing-key.json",
            "GET /api/spoolman/vendors",
            nameof(GetSpoolmanInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                Assert.Equal("Minimal Vendor", JsonContractAssertions.AssertProperty(root[0], "name", JsonValueKind.String).GetString());
                JsonContractAssertions.AssertMissingKey(root[0], "externalId");
            });
        await CaptureGetAsync(
            client,
            "/api/spoolman/materials",
            "inventory/spoolman-materials.missing-key.json",
            "GET /api/spoolman/materials",
            nameof(GetSpoolmanInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                Assert.Equal("Minimal Material", JsonContractAssertions.AssertProperty(root[0], "name", JsonValueKind.String).GetString());
                AssertMissingKeys(root[0], "density", "colorHex");
            });
    }

    /// <summary>Empty collection variants for the printed-parts inventory lists consumed by iOS.</summary>
    [Fact]
    public async Task GetPartsInventoryEndpoints_EmptyCollections_MatchCorpusAsync()
    {
        using HttpClient client = await Factory.CreateAuthenticatedClientAsync(
            username: "wire-contract-parts-empty",
            email: "wire-contract-parts-empty@example.com");

        await CaptureGetAsync(
            client,
            "/api/parts-inventory?includeInactive=false",
            "inventory/parts.empty-collection.json",
            "GET /api/parts-inventory",
            nameof(GetPartsInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
        await CaptureGetAsync(
            client,
            "/api/bins?includeInactive=false",
            "inventory/bins.empty-collection.json",
            "GET /api/bins",
            nameof(GetPartsInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
        await CaptureGetAsync(
            client,
            "/api/parts-inventory/reorder",
            "inventory/reorder.empty-collection.json",
            "GET /api/parts-inventory/reorder",
            nameof(GetPartsInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
        await CaptureGetAsync(
            client,
            "/api/parts-inventory/mappings",
            "inventory/mappings.empty-collection.json",
            "GET /api/parts-inventory/mappings",
            nameof(GetPartsInventoryEndpoints_EmptyCollections_MatchCorpusAsync),
            AssertEmptyArray);
    }

    /// <summary>
    /// Populated printed-parts inventory DTOs, including adjustment/harvest enum tokens and both
    /// typed conflict envelopes decoded by the iOS APIClient.
    /// </summary>
    [Fact]
    public async Task GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync()
    {
        _state.PopulatePrintedParts();
        using HttpClient client = await Factory.CreateAdminClientAsync(
            username: "wire-contract-parts-populated",
            email: "wire-contract-parts-populated@example.com");

        await CaptureGetAsync(
            client,
            "/api/parts-inventory?includeInactive=false",
            "inventory/parts.populated.json",
            "GET /api/parts-inventory",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(1, root.GetArrayLength());
                JsonElement part = root[0];
                Assert.Equal("PF-WIRE-01", JsonContractAssertions.AssertProperty(part, "sku", JsonValueKind.String).GetString());
                Assert.Equal("BIN-WIRE-01", JsonContractAssertions.AssertProperty(part, "defaultBinCode", JsonValueKind.String).GetString());
                Assert.True(JsonContractAssertions.AssertProperty(part, "needsReorder", JsonValueKind.True).GetBoolean());
            });
        await CaptureGetAsync(
            client,
            "/api/bins?includeInactive=false",
            "inventory/bins.populated.json",
            "GET /api/bins",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(1, root.GetArrayLength());
                Assert.Equal("BIN-WIRE-01", JsonContractAssertions.AssertProperty(root[0], "code", JsonValueKind.String).GetString());
            });
        await CaptureGetAsync(
            client,
            "/api/parts-inventory/reorder",
            "inventory/reorder.populated.json",
            "GET /api/parts-inventory/reorder",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(1, root.GetArrayLength());
                Assert.Equal(3, JsonContractAssertions.AssertProperty(root[0], "deficit", JsonValueKind.Number).GetInt32());
                _ = JsonContractAssertions.AssertProperty(root[0], "defaultBinCode", JsonValueKind.String);
            });
        await CaptureGetAsync(
            client,
            "/api/parts-inventory/mappings",
            "inventory/mappings.populated.json",
            "GET /api/parts-inventory/mappings",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal(1, root.GetArrayLength());
                _ = JsonContractAssertions.AssertProperty(root[0], "gcodeFileId", JsonValueKind.String);
                JsonContractAssertions.AssertMissingKey(root[0], "printProjectFileId");
            });

        await CapturePostAsync(
            client,
            "/api/parts-inventory/PF-WIRE-01/adjust",
            new
            {
                delta = -1,
                reason = "qc-reject",
                jobId = InventoryContractState.PrintJobId,
                binCode = "BIN-WIRE-01",
                notes = "Wire contract QC rejection",
                operationKey = "wire-contract-adjustment",
            },
            HttpStatusCode.OK,
            "inventory/adjustment.populated.json",
            "POST /api/parts-inventory/{sku}/adjust",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal("qc-reject", JsonContractAssertions.AssertProperty(root, "reason", JsonValueKind.String).GetString());
                Assert.Equal(-1, JsonContractAssertions.AssertProperty(root, "delta", JsonValueKind.Number).GetInt32());
                _ = JsonContractAssertions.AssertProperty(root, "operationKey", JsonValueKind.String);
            });

        _state.HarvestResult = _state.SuccessfulHarvest;
        await CapturePostAsync(
            client,
            $"/api/job-queue/{InventoryContractState.PrintJobId}/harvest",
            new HarvestJobRequest(
                BinCode: "BIN-WIRE-01",
                Outputs: [new HarvestOutputRequestItem("PF-WIRE-01", 2)],
                OperationKey: "wire-contract-harvest"),
            HttpStatusCode.OK,
            "inventory/harvest.populated.json",
            "POST /api/job-queue/{id}/harvest",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                JsonElement adjustments = JsonContractAssertions.AssertNonEmptyCollection(root, "adjustments");
                Assert.Equal("harvest", JsonContractAssertions.AssertProperty(adjustments[0], "reason", JsonValueKind.String).GetString());
                JsonElement outputs = JsonContractAssertions.AssertNonEmptyCollection(root, "outputs");
                Assert.Equal("ExplicitOutputs", JsonContractAssertions.AssertProperty(outputs[0], "origin", JsonValueKind.String).GetString());
            });

        _state.HarvestResult = _state.WrongBinHarvest;
        await CapturePostAsync(
            client,
            $"/api/job-queue/{InventoryContractState.WrongBinJobId}/harvest",
            new HarvestJobRequest(BinCode: "BIN-SCANNED"),
            HttpStatusCode.Conflict,
            "inventory/harvest.wrong-bin.json",
            "POST /api/job-queue/{id}/harvest (wrongBin)",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal("wrongBin", JsonContractAssertions.AssertProperty(root, "code", JsonValueKind.String).GetString());
                _ = JsonContractAssertions.AssertNonEmptyCollection(root, "mismatches");
            });

        _state.HarvestResult = _state.MappingRequiredHarvest;
        await CapturePostAsync(
            client,
            $"/api/job-queue/{InventoryContractState.MappingRequiredJobId}/harvest",
            new HarvestJobRequest(),
            HttpStatusCode.Conflict,
            "inventory/harvest.part-mapping-required.json",
            "POST /api/job-queue/{id}/harvest (partMappingRequired)",
            nameof(GetPartsInventoryEndpoints_PopulatedResponses_MatchCorpusAsync),
            root =>
            {
                Assert.Equal("partMappingRequired", JsonContractAssertions.AssertProperty(root, "code", JsonValueKind.String).GetString());
                _ = JsonContractAssertions.AssertProperty(root, "guidance", JsonValueKind.String);
                _ = JsonContractAssertions.AssertProperty(root, "projectFileId", JsonValueKind.String);
                _ = JsonContractAssertions.AssertProperty(root, "gcodeFileId", JsonValueKind.Null);
            });
    }

    /// <summary>Missing-key variants for nullable printed-parts inventory fields consumed by iOS.</summary>
    [Fact]
    public async Task GetPartsInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync()
    {
        _state.PopulatePrintedPartsMissingKeys();
        using HttpClient client = await Factory.CreateAdminClientAsync(
            username: "wire-contract-parts-missing-keys",
            email: "wire-contract-parts-missing-keys@example.com");

        await CaptureGetAsync(
            client,
            "/api/parts-inventory?includeInactive=false",
            "inventory/parts.missing-key.json",
            "GET /api/parts-inventory",
            nameof(GetPartsInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                JsonElement part = root[0];
                Assert.Equal("PF-MINIMAL-01", JsonContractAssertions.AssertProperty(part, "sku", JsonValueKind.String).GetString());
                AssertMissingKeys(part, "description", "modelFileRef", "defaultBinId", "defaultBinCode", "defaultBinName");
            });
        await CaptureGetAsync(
            client,
            "/api/bins?includeInactive=false",
            "inventory/bins.missing-key.json",
            "GET /api/bins",
            nameof(GetPartsInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                JsonElement bin = root[0];
                Assert.Equal("BIN-MINIMAL-01", JsonContractAssertions.AssertProperty(bin, "code", JsonValueKind.String).GetString());
                AssertMissingKeys(bin, "location", "notes");
            });
        await CapturePostAsync(
            client,
            "/api/parts-inventory/PF-MINIMAL-01/adjust",
            new
            {
                delta = 1,
                reason = "manual",
            },
            HttpStatusCode.OK,
            "inventory/adjustment.missing-key.json",
            "POST /api/parts-inventory/{sku}/adjust",
            nameof(GetPartsInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                Assert.Equal("manual", JsonContractAssertions.AssertProperty(root, "reason", JsonValueKind.String).GetString());
                AssertMissingKeys(root, "binId", "binCode", "printJobId", "operationKey", "notes", "userId");
            });
        await CapturePostAsync(
            client,
            $"/api/job-queue/{InventoryContractState.PrintJobId}/harvest",
            new HarvestJobRequest(),
            HttpStatusCode.OK,
            "inventory/harvest.missing-key.json",
            "POST /api/job-queue/{id}/harvest",
            nameof(GetPartsInventoryEndpoints_MissingOptionalKeys_MatchCorpusAsync),
            root =>
            {
                AssertMissingKeys(root, "binId", "binCode");
                JsonElement adjustment = JsonContractAssertions.AssertNonEmptyCollection(root, "adjustments")[0];
                AssertMissingKeys(adjustment, "binId", "binCode", "printJobId", "operationKey", "notes", "userId");
                JsonElement output = JsonContractAssertions.AssertNonEmptyCollection(root, "outputs")[0];
                Assert.Equal("JobSnapshot", JsonContractAssertions.AssertProperty(output, "origin", JsonValueKind.String).GetString());
                AssertMissingKeys(output, "expectedBinId", "expectedBinCode", "sourceFileId", "sourceMappingId", "overrideReason");
            });
    }

    /// <summary>Non-string adjustment reasons fail model binding instead of escaping as server errors.</summary>
    [Fact]
    public async Task AdjustPartInventory_NonStringReason_ReturnsBadRequestAsync()
    {
        using HttpClient client = await Factory.CreateAdminClientAsync(
            username: "wire-contract-adjustment-invalid-reason",
            email: "wire-contract-adjustment-invalid-reason@example.com");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/parts-inventory/PF-WIRE-01/adjust",
            new
            {
                delta = -1,
                reason = 1,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task CaptureGetAsync(
        HttpClient client,
        string requestPath,
        string fixturePath,
        string endpoint,
        string producingTest,
        Action<JsonElement> assertPayload)
    {
        using HttpResponseMessage response = await client.GetAsync(requestPath);
        await CaptureResponseAsync(
            response,
            HttpStatusCode.OK,
            fixturePath,
            endpoint,
            producingTest,
            assertPayload);
    }

    private static async Task CapturePostAsync<TBody>(
        HttpClient client,
        string requestPath,
        TBody body,
        HttpStatusCode expectedStatus,
        string fixturePath,
        string endpoint,
        string producingTest,
        Action<JsonElement> assertPayload)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(requestPath, body);
        await CaptureResponseAsync(
            response,
            expectedStatus,
            fixturePath,
            endpoint,
            producingTest,
            assertPayload);
    }

    private static async Task CaptureResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string fixturePath,
        string endpoint,
        string producingTest,
        Action<JsonElement> assertPayload)
    {
        string json = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected HTTP {(int)expectedStatus} ({expectedStatus}), received " +
            $"{(int)response.StatusCode} ({response.StatusCode}). Body: {json}");
        using JsonDocument document = JsonDocument.Parse(json);
        assertPayload(document.RootElement);

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            fixturePath,
            endpoint,
            $"{typeof(InventoryContractTests).FullName}.{producingTest}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    private static void AssertEmptyPagedResult(JsonElement root)
    {
        JsonContractAssertions.AssertEmptyCollection(root, "items");
        Assert.Equal(0, JsonContractAssertions.AssertProperty(root, "totalCount", JsonValueKind.Number).GetInt32());
    }

    private static void AssertEmptyArray(JsonElement root)
    {
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(0, root.GetArrayLength());
    }

    private static void AssertMissingKeys(JsonElement root, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            JsonContractAssertions.AssertMissingKey(root, propertyName);
        }
    }

    private sealed class InventoryContractFactory(InventoryContractState state)
        : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                Replace(services, CreateSpoolmanService(state));
                Replace(services, CreatePartRepository(state));
                Replace(services, CreateBinRepository(state));
                Replace(services, CreateAdjustmentRepository());
                Replace(services, CreateMappingRepository(state));
                Replace(services, CreatePartInventoryService(state));
                Replace(services, CreateReorderService(state));
                Replace(services, CreateBarcodeScanLogService());
                Replace(services, CreateHarvestService(state));
                Replace(services, CreateFeatureGate());
            });
        }

        private static void Replace<TService>(IServiceCollection services, TService implementation)
            where TService : class
        {
            services.RemoveAll<TService>();
            services.AddSingleton(implementation);
        }

        private static ISpoolmanService CreateSpoolmanService(InventoryContractState state)
        {
            var service = new Mock<ISpoolmanService>();
            service
                .Setup(value => value.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => SpoolmanReadResult.Ok<SpoolmanSpoolDto>(state.Spools, state.Spools.Count));
            service
                .Setup(value => value.ListFilamentsPagedAsync(It.IsAny<SpoolmanFilamentQueryParams>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new SpoolmanPagedResult<SpoolmanFilamentDto>(state.Filaments, state.Filaments.Count));
            service
                .Setup(value => value.ListVendorsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.Vendors);
            service
                .Setup(value => value.ListMaterialsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.Materials);
            service
                .Setup(value => value.GetAvailableMaterialsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.AvailableMaterials);
            service
                .Setup(value => value.GetConfig())
                .Returns(new SpoolmanConfigDto("http://wire-contract-spoolman.test"));
            service
                .Setup(value => value.HealthProbeAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpoolmanProbeResult(true, "http://wire-contract-spoolman.test"));
            return service.Object;
        }

        private static IPartInventoryRepository CreatePartRepository(InventoryContractState state)
        {
            var repository = new Mock<IPartInventoryRepository>();
            repository
                .Setup(value => value.GetAllAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.Parts.ToList());
            repository
                .Setup(value => value.GetBySkuAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string sku, CancellationToken _) =>
                    state.Parts.SingleOrDefault(part => string.Equals(part.Sku, sku, StringComparison.OrdinalIgnoreCase)));
            return repository.Object;
        }

        private static IBinRepository CreateBinRepository(InventoryContractState state)
        {
            var repository = new Mock<IBinRepository>();
            repository
                .Setup(value => value.GetAllAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.Bins.ToList());
            repository
                .Setup(value => value.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string code, CancellationToken _) =>
                    state.Bins.SingleOrDefault(bin => string.Equals(bin.Code, code, StringComparison.OrdinalIgnoreCase)));
            return repository.Object;
        }

        private static IPartInventoryAdjustmentRepository CreateAdjustmentRepository()
        {
            var repository = new Mock<IPartInventoryAdjustmentRepository>();
            repository
                .Setup(value => value.GetForPartAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            return repository.Object;
        }

        private static IPartOutputMappingRepository CreateMappingRepository(InventoryContractState state)
        {
            var repository = new Mock<IPartOutputMappingRepository>();
            repository
                .Setup(value => value.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.Mappings.ToList());
            repository
                .Setup(value => value.GetForPartAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid partId, CancellationToken _) =>
                    state.Mappings.Where(mapping => mapping.PartInventoryId == partId).ToList());
            return repository.Object;
        }

        private static IPartInventoryService CreatePartInventoryService(InventoryContractState state)
        {
            var service = new Mock<IPartInventoryService>();
            service
                .Setup(value => value.AdjustAsync(
                    It.IsAny<string>(),
                    It.IsAny<AdjustCommand>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.AdjustResult);
            return service.Object;
        }

        private static IReorderEvaluationService CreateReorderService(InventoryContractState state)
        {
            var service = new Mock<IReorderEvaluationService>();
            service
                .Setup(value => value.GetReorderCandidatesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.ReorderCandidates);
            return service.Object;
        }

        private static IBarcodeScanLogService CreateBarcodeScanLogService()
        {
            var service = new Mock<IBarcodeScanLogService>();
            service
                .Setup(value => value.LogAsync(It.IsAny<BarcodeScanLog>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            return service.Object;
        }

        private static IPartHarvestService CreateHarvestService(InventoryContractState state)
        {
            var service = new Mock<IPartHarvestService>();
            service
                .Setup(value => value.HarvestJobAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<HarvestJobRequest>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => state.HarvestResult);
            return service.Object;
        }

        private static IOperatorFeatureGate CreateFeatureGate()
        {
            var gate = new Mock<IOperatorFeatureGate>();
            gate.Setup(value => value.IsEnabled(It.IsAny<OperatorFeature>())).Returns(true);
            gate.Setup(value => value.IsEnabledAsync(It.IsAny<OperatorFeature>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            gate.Setup(value => value.IsEnabledStrictAsync(It.IsAny<OperatorFeature>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            gate.Setup(value => value.IsHardDisabledByEnvironment(It.IsAny<OperatorFeature>())).Returns(false);
            gate.Setup(value => value.GetFlagName(It.IsAny<OperatorFeature>())).Returns((OperatorFeature feature) => feature.ToString());
            return gate.Object;
        }
    }

    private sealed class InventoryContractState
    {
        internal static readonly Guid PartId = Guid.Parse("2d2cb44c-6958-493f-99c8-7bd62acfe847");
        internal static readonly Guid BinId = Guid.Parse("c411ea64-3137-4c40-b08c-5cb71f018b55");
        internal static readonly Guid MappingId = Guid.Parse("fe7118ee-63f7-4d70-aa19-531ca37cb008");
        internal static readonly Guid GcodeFileId = Guid.Parse("44959ed9-49db-4911-83a0-bad7793075df");
        internal static readonly Guid AdjustmentId = Guid.Parse("7b6476f4-d81d-445f-aad4-23e5b8d0f21f");
        internal static readonly Guid HarvestAdjustmentId = Guid.Parse("ed4f602d-d007-4c29-81b6-8dd3e0c34a37");
        internal static readonly Guid PrintJobId = Guid.Parse("49ad99f7-80f3-45ee-884c-6f713ca3af5d");
        internal static readonly Guid WrongBinJobId = Guid.Parse("54672235-90c8-46e2-b1c2-5bd184f3caf6");
        internal static readonly Guid MappingRequiredJobId = Guid.Parse("4c694fd3-4936-4184-b376-d504842f2985");
        internal static readonly Guid ProjectFileId = Guid.Parse("1d313e87-072f-49df-a6d0-2e9cc61ac6cd");
        private static readonly DateTime Timestamp = new(2026, 8, 30, 12, 34, 56, DateTimeKind.Utc);

        internal IReadOnlyList<SpoolmanSpoolDto> Spools { get; private set; } = [];
        internal IReadOnlyList<SpoolmanFilamentDto> Filaments { get; private set; } = [];
        internal IReadOnlyList<SpoolmanVendorDto> Vendors { get; private set; } = [];
        internal IReadOnlyList<SpoolmanMaterialDto> Materials { get; private set; } = [];
        internal IReadOnlyList<string> AvailableMaterials { get; private set; } = [];
        internal IReadOnlyList<PartInventory> Parts { get; private set; } = [];
        internal IReadOnlyList<Bin> Bins { get; private set; } = [];
        internal IReadOnlyList<PartOutputMapping> Mappings { get; private set; } = [];
        internal IReadOnlyList<ReorderCandidateResponse> ReorderCandidates { get; private set; } = [];
        internal AdjustResult AdjustResult { get; private set; } =
            new(PartInventoryOutcome.PartNotFound, null, 0, "Not populated.");
        internal HarvestResult HarvestResult { get; set; } =
            new(PartInventoryOutcome.JobNotFound, null, "Not populated.");
        internal HarvestResult SuccessfulHarvest { get; private set; } =
            new(PartInventoryOutcome.JobNotFound, null, "Not populated.");
        internal HarvestResult WrongBinHarvest { get; private set; } =
            new(PartInventoryOutcome.JobNotFound, null, "Not populated.");
        internal HarvestResult MappingRequiredHarvest { get; private set; } =
            new(PartInventoryOutcome.JobNotFound, null, "Not populated.");

        internal void PopulateSpoolman()
        {
            Spools =
            [
                new SpoolmanSpoolDto(
                    Id: 501,
                    Name: "Wire Contract PLA Spool",
                    Material: "PLA",
                    RemainingWeightG: 640.5,
                    ColorHex: "3366CC",
                    InUse: true,
                    FilamentName: "Wire Contract PLA",
                    Vendor: "Wire Contract Vendor",
                    RegisteredAt: Timestamp,
                    FirstUsedAt: Timestamp.AddDays(1),
                    LastUsedAt: Timestamp.AddDays(2),
                    InitialWeightG: 1000,
                    UsedWeightG: 359.5,
                    SpoolWeightG: 220,
                    RemainingLengthMm: 214000,
                    UsedLengthMm: 120000,
                    Location: "Rack A",
                    LotNumber: "LOT-WIRE-01",
                    Archived: false,
                    Price: 24.95,
                    Comment: "Canonical mobile inventory spool",
                    FilamentId: 77),
            ];
            Filaments =
            [
                new SpoolmanFilamentDto(
                    Id: 77,
                    Name: "Wire Contract PLA",
                    Material: "PLA",
                    ColorHex: "3366CC",
                    Vendor: "Wire Contract Vendor",
                    Density: 1.24,
                    Diameter: 1.75,
                    Weight: 1000,
                    SpoolWeight: 220,
                    Price: 24.95,
                    SettingsExtruderTemp: 215,
                    SettingsBedTemp: 60,
                    ArticleNumber: "WIRE-PLA-1000",
                    Comment: "Canonical mobile inventory filament",
                    MultiColorHexes: "3366CC,FFFFFF",
                    ExternalId: "wire-contract-filament",
                    Gtin: "00012345678905"),
            ];
            Vendors = [new SpoolmanVendorDto(12, "Wire Contract Vendor", "wire-contract-vendor")];
            Materials = [new SpoolmanMaterialDto(5, "PLA", 1.24, "3366CC")];
            AvailableMaterials = ["ASA", "PLA"];
        }

        internal void PopulateSpoolmanMissingKeys()
        {
            Spools = [new SpoolmanSpoolDto(502, "Minimal Spool", "PLA", null, null, InUse: false)];
            Filaments =
            [
                new SpoolmanFilamentDto(
                    78,
                    Name: null,
                    Material: null,
                    ColorHex: null,
                    Vendor: null,
                    Density: null,
                    Diameter: null,
                    Weight: null,
                    SpoolWeight: null,
                    Price: null,
                    SettingsExtruderTemp: null,
                    SettingsBedTemp: null,
                    ArticleNumber: null,
                    Comment: null,
                    MultiColorHexes: null,
                    ExternalId: null),
            ];
            Vendors = [new SpoolmanVendorDto(13, "Minimal Vendor", null)];
            Materials = [new SpoolmanMaterialDto(6, "Minimal Material")];
        }

        internal void PopulatePrintedParts()
        {
            var bin = new Bin
            {
                Id = BinId,
                Code = "BIN-WIRE-01",
                Name = "Wire Contract Finished Parts",
                Location = "Aisle 4 / Shelf B",
                Notes = "Canonical mobile inventory bin",
                IsActive = true,
                CreatedAt = Timestamp,
                UpdatedAt = Timestamp.AddHours(1),
            };
            var part = new PartInventory
            {
                Id = PartId,
                Sku = "PF-WIRE-01",
                Name = "Wire Contract Bracket",
                Description = "Canonical printed-part inventory item",
                ModelFileRef = "models/wire-contract-bracket.3mf",
                DefaultBinId = BinId,
                DefaultBin = bin,
                OnHand = 2,
                ReorderPoint = 5,
                IsActive = true,
                CreatedAt = Timestamp,
                UpdatedAt = Timestamp.AddHours(2),
            };
            var mapping = new PartOutputMapping
            {
                Id = MappingId,
                PartInventoryId = PartId,
                PartInventory = part,
                GcodeFileId = GcodeFileId,
                Quantity = 2,
                CreatedAt = Timestamp,
                UpdatedAt = Timestamp.AddHours(3),
            };
            var adjustment = new PartAdjustmentResponse(
                AdjustmentId,
                PartId,
                "PF-WIRE-01",
                BinId,
                "BIN-WIRE-01",
                -1,
                1,
                PartAdjustmentReason.QcReject,
                PrintJobId,
                "wire-contract-adjustment",
                "Wire contract QC rejection",
                "wire-contract-user",
                Timestamp.AddHours(4));
            var harvestAdjustment = new PartAdjustmentResponse(
                HarvestAdjustmentId,
                PartId,
                "PF-WIRE-01",
                BinId,
                "BIN-WIRE-01",
                2,
                4,
                PartAdjustmentReason.Harvest,
                PrintJobId,
                "wire-contract-harvest",
                "Canonical harvest",
                "wire-contract-user",
                Timestamp.AddHours(5));
            var harvestOutput = new HarvestOutputResponse(
                Sequence: 1,
                PartInventoryId: PartId,
                PartSku: "PF-WIRE-01",
                Quantity: 2,
                ExpectedBinId: BinId,
                ExpectedBinCode: "BIN-WIRE-01",
                ActualBinId: BinId,
                ActualBinCode: "BIN-WIRE-01",
                Origin: PartHarvestOutputOrigin.ExplicitOutputs,
                SourceFileId: GcodeFileId,
                SourceMappingId: MappingId,
                OverrideApplied: true,
                OverrideReason: "Operator confirmed canonical destination",
                CreatedAt: Timestamp.AddHours(5));

            Parts = [part];
            Bins = [bin];
            Mappings = [mapping];
            ReorderCandidates =
            [
                new ReorderCandidateResponse(
                    PartId,
                    "PF-WIRE-01",
                    "Wire Contract Bracket",
                    OnHand: 2,
                    ReorderPoint: 5,
                    Deficit: 3,
                    DefaultBinId: BinId,
                    DefaultBinCode: "BIN-WIRE-01",
                    DefaultBinName: "Wire Contract Finished Parts"),
            ];
            AdjustResult = new AdjustResult(PartInventoryOutcome.Ok, adjustment, 1, null);
            SuccessfulHarvest = new HarvestResult(
                PartInventoryOutcome.Ok,
                new HarvestJobResponse(
                    PrintJobId,
                    Timestamp.AddHours(5),
                    BinId,
                    "BIN-WIRE-01",
                    AlreadyHarvested: false,
                    Adjustments: [harvestAdjustment],
                    Outputs: [harvestOutput]),
                null);
            WrongBinHarvest = new HarvestResult(
                PartInventoryOutcome.WrongBin,
                null,
                "The scanned bin does not match the expected destination.",
                WrongBin: new WrongBinResponse(
                [
                    new WrongBinMismatchResponse(
                        "PF-WIRE-01",
                        "BIN-WIRE-01",
                        "BIN-SCANNED"),
                ]));
            MappingRequiredHarvest = new HarvestResult(
                PartInventoryOutcome.NoMappings,
                null,
                "Printed-part output mapping is required.",
                MappingRequired: new PartMappingRequiredResponse(
                    MappingRequiredJobId,
                    ProjectFileId,
                    null,
                    "Map the project output to a printed-part SKU before harvesting."));
            HarvestResult = SuccessfulHarvest;
        }

        internal void PopulatePrintedPartsMissingKeys()
        {
            var bin = new Bin
            {
                Id = BinId,
                Code = "BIN-MINIMAL-01",
                Name = "Minimal Bin",
                IsActive = true,
                CreatedAt = Timestamp,
                UpdatedAt = Timestamp.AddHours(1),
            };
            var part = new PartInventory
            {
                Id = PartId,
                Sku = "PF-MINIMAL-01",
                Name = "Minimal Part",
                OnHand = 1,
                ReorderPoint = 0,
                IsActive = true,
                CreatedAt = Timestamp,
                UpdatedAt = Timestamp.AddHours(2),
            };
            var adjustment = new PartAdjustmentResponse(
                AdjustmentId,
                PartId,
                "PF-MINIMAL-01",
                BinId: null,
                BinCode: null,
                Delta: 1,
                ResultingBalance: 2,
                PartAdjustmentReason.Manual,
                PrintJobId: null,
                OperationKey: null,
                Notes: null,
                UserId: null,
                Timestamp.AddHours(3));
            var harvestAdjustment = new PartAdjustmentResponse(
                HarvestAdjustmentId,
                PartId,
                "PF-MINIMAL-01",
                BinId: null,
                BinCode: null,
                Delta: 1,
                ResultingBalance: 2,
                PartAdjustmentReason.Harvest,
                PrintJobId: null,
                OperationKey: null,
                Notes: null,
                UserId: null,
                Timestamp.AddHours(4));
            var harvestOutput = new HarvestOutputResponse(
                Sequence: 1,
                PartInventoryId: PartId,
                PartSku: "PF-MINIMAL-01",
                Quantity: 1,
                ExpectedBinId: null,
                ExpectedBinCode: null,
                ActualBinId: BinId,
                ActualBinCode: "BIN-MINIMAL-01",
                Origin: PartHarvestOutputOrigin.JobSnapshot,
                SourceFileId: null,
                SourceMappingId: null,
                OverrideApplied: false,
                OverrideReason: null,
                CreatedAt: Timestamp.AddHours(4));

            Parts = [part];
            Bins = [bin];
            AdjustResult = new AdjustResult(PartInventoryOutcome.Ok, adjustment, 2, null);
            SuccessfulHarvest = new HarvestResult(
                PartInventoryOutcome.Ok,
                new HarvestJobResponse(
                    PrintJobId,
                    Timestamp.AddHours(4),
                    BinId: null,
                    BinCode: null,
                    AlreadyHarvested: false,
                    Adjustments: [harvestAdjustment],
                    Outputs: [harvestOutput]),
                null);
            HarvestResult = SuccessfulHarvest;
        }
    }
}
