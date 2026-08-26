extern alias OrcaWorker;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using WorkerServices = OrcaWorker::Farm.OrcaSlicer.Worker.Services;

namespace Farm.Slicer.Module.Tests.Integration;

public sealed class ProfileFamilyHttpContractTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task CloneFamily_MissingRequiredFields_ReturnsFeatureErrorEnvelope()
    {
        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-validation-admin",
            "profile-family-validation@example.com");

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone-family",
            new { targetPrinterModelId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("code").GetString().Should().Be("invalid_profile_family");
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("errors", out _).Should().BeFalse();
    }
}

public sealed class ProfileFamilyCloneLookupAcceptanceTests
{
    private const string SourceManufacturer = "Prusa";
    private const string SourceModel = "Prusa Test";
    private const string SourceMachine = "Prusa Test 0.4 nozzle";
    private const string FamilyName = "Farm Acceptance";

    [Fact]
    public async Task CloneFamily_ThenForModelLookup_ReturnsRenderedMachineProfile()
    {
        string root = Path.Join(
            AppContext.BaseDirectory,
            "profile-family-acceptance",
            Guid.NewGuid().ToString("N"));
        try
        {
            await RunAcceptanceAsync(root);
        }
        finally
        {
            string cacheDatabasePath = Path.Join(root, "profile-cache.db");
            using var pooledConnection = new SqliteConnection(
                $"Data Source={cacheDatabasePath};Mode=ReadWriteCreate;Cache=Shared");
            SqliteConnection.ClearPool(pooledConnection);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task RunAcceptanceAsync(string root)
    {
        string stockPath = Path.Join(root, "stock");
        string overlayPath = Path.Join(root, "overlay");
        string customPath = Path.Join(root, "custom");
        WriteStockProfiles(stockPath);
        WriteStockProfiles(overlayPath);

        await using var bundleStore = new WorkerServices.CustomProfileBundleStore(
            NullLogger<WorkerServices.CustomProfileBundleStore>.Instance,
            stockPath,
            overlayPath,
            customPath);
        await using var workerProfiles = new WorkerServices.CachedOrcaProfilesService(
            NullLogger<WorkerServices.CachedOrcaProfilesService>.Instance,
            overlayPath,
            Path.Join(root, "profile-cache.db"),
            customPath);
        await workerProfiles.InitializeAsync();

        var workerClient = new InProcessProfileFamilyWorkerClient(
            CreateCatalog(),
            bundleStore,
            workerProfiles);
        using var workerHandler = new WorkerLookupHandler(workerProfiles);
        await using var factory = new AcceptanceFactory(workerClient, workerHandler);
        await factory.ResetDatabaseAsync();

        Guid targetModelId = Guid.NewGuid();
        await SeedTargetModelAndWorkerAsync(factory, targetModelId);
        using HttpClient client = await factory.CreateAdminClientAsync(
            "profile-family-acceptance-admin",
            "profile-family-acceptance@example.com");

        using HttpResponseMessage before = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        before.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (JsonDocument beforeBody = JsonDocument.Parse(
                   await before.Content.ReadAsStringAsync()))
        {
            beforeBody.RootElement.GetProperty("code").GetString()
                .Should().Be("no_profiles_for_model");
        }

        var request = new CloneProfileFamilyRequestDto
        {
            FamilyName = FamilyName,
            TargetPrinterModelId = targetModelId,
            SourceManufacturer = SourceManufacturer,
            SourceMachineModelName = SourceModel,
            NozzleDiameters = [0.4]
        };
        using HttpResponseMessage clone = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone-family",
            request);
        clone.StatusCode.Should().Be(HttpStatusCode.Created);

        using HttpResponseMessage after = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        List<MachineProfileDto>? profiles =
            await after.Content.ReadFromJsonAsync<List<MachineProfileDto>>();
        profiles.Should().ContainSingle(profile =>
            profile.Name == $"{FamilyName} 0.4 nozzle" &&
            profile.PrinterModel == FamilyName);
    }

    private static async Task SeedTargetModelAndWorkerAsync(
        CustomWebApplicationFactory factory,
        Guid targetModelId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid manufacturerId = Guid.NewGuid();
        appDb.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Farm Catalog"
        });
        appDb.PrinterModels.Add(new PrinterModel
        {
            Id = targetModelId,
            ManufacturerId = manufacturerId,
            Name = "Catalog Target"
        });
        await appDb.SaveChangesAsync();

        SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        slicerDb.SlicerServices.Add(new SlicerService
        {
            Id = Guid.NewGuid(),
            Name = "acceptance-worker",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Host = "http://worker",
            Status = "Online",
            LastSeen = DateTime.UtcNow,
            CapabilitiesJson =
                $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
        });
        await slicerDb.SaveChangesAsync();
    }

    private static AllProfilesResponseDto CreateCatalog()
    {
        var machine = new MachineProfileDto
        {
            Name = SourceMachine,
            Manufacturer = SourceManufacturer,
            PrinterModel = SourceModel,
            NozzleDiameter = 0.4,
            Settings = new Dictionary<string, object>
            {
                ["name"] = SourceMachine,
                ["printer_model"] = SourceModel,
                ["nozzle_diameter"] = new List<string> { "0.4" },
                ["max_layer_height"] = new List<string> { "0.32" }
            }
        };
        var model = new PrinterModelProfilesDto
        {
            Name = SourceModel,
            ModelId = "PRUSA_TEST",
            MachineProfiles = [machine]
        };
        return new AllProfilesResponseDto
        {
            ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>
            {
                [SourceManufacturer] = new()
                {
                    Name = SourceManufacturer,
                    Models = new Dictionary<string, PrinterModelProfilesDto>
                    {
                        [SourceModel] = model
                    }
                }
            },
            MachineModelProfiles = new Dictionary<string, IList<MachineModelProfileDto>>
            {
                [SourceManufacturer] =
                [
                    new MachineModelProfileDto
                    {
                        Name = SourceModel,
                        Manufacturer = SourceManufacturer
                    }
                ]
            }
        };
    }

    private static void WriteStockProfiles(string profilesPath)
    {
        string machinePath = Path.Join(profilesPath, SourceManufacturer, "machine");
        Directory.CreateDirectory(machinePath);
        File.WriteAllText(
            Path.Join(profilesPath, $"{SourceManufacturer}.json"),
            """
            {
              "name": "Prusa",
              "version": "01.00.00.00",
              "machine_model_list": [],
              "machine_list": [
                {
                  "name": "Prusa Test 0.4 nozzle",
                  "sub_path": "machine/Prusa Test 0.4 nozzle.json"
                }
              ],
              "process_list": [],
              "filament_list": []
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(machinePath, $"{SourceMachine}.json"),
            """
            {
              "type": "machine",
              "name": "Prusa Test 0.4 nozzle",
              "from": "system",
              "instantiation": "true",
              "printer_model": "Prusa Test",
              "nozzle_diameter": ["0.4"],
              "max_layer_height": ["0.32"]
            }
            """,
            Encoding.UTF8);
    }

    private sealed class AcceptanceFactory(
        IProfileFamilyWorkerClient workerClient,
        HttpMessageHandler workerHandler) : CustomWebApplicationFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<IProfileFamilyWorkerClient>();
            services.AddSingleton(workerClient);
            services.RemoveAll<HttpClient>();
            services.AddSingleton(new HttpClient(workerHandler, disposeHandler: false));
        }
    }

    private sealed class InProcessProfileFamilyWorkerClient(
        AllProfilesResponseDto catalog,
        WorkerServices.CustomProfileBundleStore bundleStore,
        WorkerServices.CachedOrcaProfilesService profilesService) : IProfileFamilyWorkerClient
    {
        public Task<(ProfileFamilyWorkerTarget Target, AllProfilesResponseDto Catalog)> GetCatalogAsync(
            string sourceManufacturer,
            string? orcaVersion,
            CancellationToken ct)
        {
            sourceManufacturer.Should().Be(SourceManufacturer);
            return Task.FromResult((
                new ProfileFamilyWorkerTarget("http://worker", orcaVersion ?? "2.4.2"),
                catalog));
        }

        public async Task WriteBundleAsync(
            ProfileFamilyWorkerTarget target,
            ProfileFamilyBundleDto bundle,
            CancellationToken ct)
        {
            JsonElement manifest = JsonSerializer.Deserialize<JsonElement>(bundle.ManifestJson);
            List<WorkerServices.CustomProfileFileRequest> files = bundle.Files
                .Select(file => new WorkerServices.CustomProfileFileRequest(
                    file.RelativePath,
                    bundle.FamilyName,
                    JsonSerializer.Deserialize<JsonElement>(file.Content)))
                .ToList();
            await bundleStore.InstallAsync(
                $"PrintFarmer-{bundle.FamilyId:N}",
                new WorkerServices.CustomProfileBundleRequest(manifest, files),
                ct);
            WorkerServices.ProfileReloadResult reload =
                await profilesService.ReloadProfilesAsync(ct);
            if (reload.Failures.Count > 0)
            {
                throw new ProfileFamilySourceException(
                    $"Worker rejected {reload.Failures.Count} rendered profile(s).");
            }
        }
    }

    private sealed class WorkerLookupHandler(
        WorkerServices.CachedOrcaProfilesService profilesService) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string route = "/api/profiles/machine/";
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method != HttpMethod.Get ||
                !path.StartsWith(route, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            string printerModel = Uri.UnescapeDataString(path[route.Length..]);
            List<MachineProfileDto> profiles =
                await profilesService.GetMachineProfilesByPrinterModelAsync(
                    printerModel,
                    cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(profiles)
            };
        }
    }
}
