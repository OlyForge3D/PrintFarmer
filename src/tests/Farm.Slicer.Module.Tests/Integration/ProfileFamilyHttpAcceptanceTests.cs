using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

[Collection(IntegrationTestCollection.Name)]
public sealed class ProfileFamilyHttpContractTests(CustomWebApplicationFactory factory)
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

[Collection(IntegrationTestCollection.Name)]
public sealed class ProfileFamilyCloneLookupAcceptanceTests
{
    private const string SourceManufacturer = "Prusa";
    private const string SourceModel = "Prusa Test";
    private const string SourceMachine = "Prusa Test 0.4 nozzle";
    private const string FamilyName = "Farm Acceptance";

    [Fact]
    public async Task CloneFamily_ThenForModelLookup_ReturnsRenderedMachineProfile()
    {
        await RunAcceptanceAsync();
    }

    private static async Task RunAcceptanceAsync()
    {
        var installedProfiles = new InMemoryRenderedProfiles();
        var workerClient = new InProcessProfileFamilyWorkerClient(
            CreateCatalog(),
            installedProfiles);
        using var workerHandler = new WorkerLookupHandler(installedProfiles);
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
        InMemoryRenderedProfiles installedProfiles) : IProfileFamilyWorkerClient
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

        public Task WriteBundleAsync(
            ProfileFamilyWorkerTarget target,
            ProfileFamilyBundleDto bundle,
            CancellationToken ct)
        {
            installedProfiles.Install(bundle);
            return Task.CompletedTask;
        }
    }

    private sealed class WorkerLookupHandler(
        InMemoryRenderedProfiles installedProfiles) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string route = "/api/profiles/machine/";
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method != HttpMethod.Get ||
                !path.StartsWith(route, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            string printerModel = Uri.UnescapeDataString(path[route.Length..]);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(installedProfiles.Get(printerModel))
            };
            return Task.FromResult(response);
        }
    }

    private sealed class InMemoryRenderedProfiles
    {
        private readonly Dictionary<string, List<MachineProfileDto>> _profiles =
            new(StringComparer.Ordinal);

        public void Install(ProfileFamilyBundleDto bundle)
        {
            _profiles.Clear();
            foreach (RenderedProfileFileDto file in bundle.Files.Where(file =>
                         file.RelativePath.StartsWith("machine/", StringComparison.Ordinal)))
            {
                using JsonDocument document = JsonDocument.Parse(file.Content);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("instantiation", out JsonElement instantiation)
                    || !string.Equals(
                        instantiation.GetString(),
                        "true",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string printerModel = root.GetProperty("printer_model").GetString()!;
                var profile = new MachineProfileDto
                {
                    Name = root.GetProperty("name").GetString()!,
                    Manufacturer = "Custom",
                    PrinterModel = printerModel,
                    Instantiation = true,
                    Inherits = root.GetProperty("inherits").GetString(),
                    NozzleDiameter = ReadNozzleDiameter(root),
                    Settings = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        root.GetRawText()) ?? []
                };
                if (!_profiles.TryGetValue(printerModel, out List<MachineProfileDto>? profiles))
                {
                    profiles = [];
                    _profiles.Add(printerModel, profiles);
                }

                profiles.Add(profile);
            }
        }

        public List<MachineProfileDto> Get(string printerModel) =>
            _profiles.TryGetValue(printerModel, out List<MachineProfileDto>? profiles)
                ? profiles
                : [];

        private static double? ReadNozzleDiameter(JsonElement profile)
        {
            if (!profile.TryGetProperty("nozzle_diameter", out JsonElement nozzleDiameters)
                || nozzleDiameters.ValueKind != JsonValueKind.Array
                || nozzleDiameters.GetArrayLength() == 0)
            {
                return null;
            }

            return double.TryParse(
                nozzleDiameters[0].GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                out double diameter)
                ? diameter
                : null;
        }
    }
}
