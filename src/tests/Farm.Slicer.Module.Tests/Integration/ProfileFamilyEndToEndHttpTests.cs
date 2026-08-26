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
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OrcaWorkerCore = OrcaWorker::Farm.Slicer.Worker.Core;
using OrcaWorkerCtrl = OrcaWorker::Farm.OrcaSlicer.Worker.Controllers;
using OrcaWorkerSvc = OrcaWorker::Farm.OrcaSlicer.Worker.Services;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// End-to-end regression coverage for issue #2073: after cloning/creating a
/// printer family via <c>POST /api/slicer/profiles/clone-family</c>, an
/// immediate <c>GET /api/slicer/profiles/machine/for-model/{modelId}</c> MUST
/// return the newly-rendered machine profile without any worker restart,
/// reconciliation tick, or reload — with real HTTP transports between the
/// API and the worker in both directions.
/// </summary>
/// <remarks>
/// This test closes the gap that both <see cref="ProfileFamilyCloneLookupAcceptanceTests"/>
/// and <c>ProfileFamilyRealHttpRoundTripTests</c> leave open. The former
/// substitutes <c>InProcessProfileFamilyWorkerClient</c> and a fake
/// <c>WorkerLookupHandler</c>, bypassing the real HTTP path in both directions;
/// the latter drives the worker's real HTTP boundary but does not exercise
/// the API-side clone flow. This test exercises BOTH: the API's typed
/// <see cref="System.Net.Http.HttpClient"/> for
/// <c>ProfileFamilyWorkerClient</c> and the plain
/// <see cref="System.Net.Http.HttpClient"/> injected into
/// <c>ProfilesController</c> both target a hosted worker
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>, so any real-HTTP
/// gap — routing, model binding, auth filter, serialization, or the
/// <c>MutateAndReloadProfilesAsync</c> invocation embedded in the
/// controller — surfaces as a failing assertion.
/// </remarks>
public sealed class ProfileFamilyEndToEndHttpTests : IAsyncDisposable
{
    private const string SharedKey = "test-worker-key";
    private const string SourceManufacturer = "Prusa";
    private const string SourceModel = "Prusa Test";
    private const string SourceMachine = "Prusa Test 0.4 nozzle";
    private const string FamilyName = "E2E Farm";
    private const string WorkerBaseUrl = "http://e2e-worker";

    private readonly string _testRoot = Path.Join(
        AppContext.BaseDirectory,
        "profile-family-e2e",
        Guid.NewGuid().ToString("N"));

    private WebApplication? _worker;
    private E2EFactory? _factory;

    [Fact]
    public async Task CloneFamily_ThenForModelLookup_UsesRealHttpAcrossApiAndWorker()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "profile-cache.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfiles(stockRoot);
        WriteStockProfiles(overlayRoot);

        HttpMessageHandler workerHandler = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);
        var recorder = new List<string>();
        var recordingHandler = new RecordingDelegatingHandler(workerHandler, recorder);

        _factory = new E2EFactory(recordingHandler);
        await _factory.ResetDatabaseAsync();

        Guid targetModelId = Guid.NewGuid();
        await SeedTargetModelAndWorkerAsync(_factory, targetModelId);

        using HttpClient client = await _factory.CreateAdminClientAsync(
            "profile-family-e2e-admin",
            "profile-family-e2e@example.com");

        using HttpResponseMessage before = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        before.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "sanity check: before the clone, no OrcaSlicer alias exists for " +
            "this catalog model, so the lookup MUST report 'no_profiles_for_model'.");
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

        string cloneBody = await clone.Content.ReadAsStringAsync();
        string recordedRequests = string.Join("\n  ", recorder);
        clone.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "the real HTTP round-trip PUT /api/profiles/custom-bundles/... " +
            "MUST succeed against the hosted worker — a non-201 here means " +
            "the API-side clone flow is failing over real HTTP (the seam " +
            "the existing acceptance test skips via InProcessProfileFamilyWorkerClient). " +
            $"Response body: {cloneBody}. Recorded HTTP requests through worker handler:\n  {recordedRequests}");

        using HttpResponseMessage after = await client.GetAsync(
            $"/api/slicer/profiles/machine/for-model/{targetModelId}");
        after.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "immediately after the clone, GET machine/for-model MUST return " +
            "200 — this is the exact user-visible symptom of issue #2073. If " +
            "this is 404 or 503 while the family is Healthy in the API DB, " +
            "the bug is a propagation gap between the install PUT and the " +
            "slice-time lookup GET over real HTTP.");
        List<MachineProfileDto>? profiles =
            await after.Content.ReadFromJsonAsync<List<MachineProfileDto>>();
        profiles.Should().NotBeNull();
        profiles!.Should().ContainSingle(profile =>
            profile.Name == $"{FamilyName} 0.4 nozzle" &&
            profile.PrinterModel == FamilyName);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync();
            await _worker.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        string cacheDatabasePath = Path.Join(_testRoot, "profile-cache.db");
        if (File.Exists(cacheDatabasePath))
        {
            using var pooledConnection = new SqliteConnection(
                $"Data Source={cacheDatabasePath};Mode=ReadWriteCreate;Cache=Shared");
            SqliteConnection.ClearPool(pooledConnection);
        }

        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch (IOException)
            {
                // Symlinks or SQLite pool cleanup races may briefly hold the
                // directory. Best-effort cleanup — CI's per-run temp directory
                // is disposed by the test harness anyway.
            }
        }
    }

    /// <summary>
    /// Stands up a real worker <see cref="WebApplication"/> hosted on a
    /// <see cref="TestServer"/>. Its <c>CustomProfilesController</c>,
    /// <c>SlicerProfilesController</c>, <c>CustomProfileBundleStore</c>,
    /// <c>CachedOrcaProfilesService</c>, and shared-key auth filter are all
    /// registered exactly as in the real worker's Program.cs, so a real HTTP
    /// PUT from the API round-trips through every real seam.
    /// </summary>
    private async Task<HttpMessageHandler> StartWorkerAsync(
        string stockRoot,
        string overlayRoot,
        string customRoot,
        string dbPath)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WorkerAuth:SharedKey"] = SharedKey
            });

        _ = builder.Services.AddControllers()
            .AddApplicationPart(typeof(OrcaWorkerCtrl.CustomProfilesController).Assembly);

        _ = builder.Services.AddSingleton(sp =>
            new OrcaWorkerSvc.CachedOrcaProfilesService(
                NullLogger<OrcaWorkerSvc.CachedOrcaProfilesService>.Instance,
                profilesPath: overlayRoot,
                dbPath: dbPath,
                customProfilesPath: customRoot));
        _ = builder.Services.AddSingleton(sp =>
            new OrcaWorkerSvc.CustomProfileBundleStore(
                NullLogger<OrcaWorkerSvc.CustomProfileBundleStore>.Instance,
                stockProfilesPath: stockRoot,
                overlayProfilesPath: overlayRoot,
                customProfilesPath: customRoot));
        _ = builder.Services.AddSingleton<OrcaWorkerSvc.CustomProfilesReconciliationState>();
        _ = builder.Services.AddSingleton<OrcaWorkerCtrl.WorkerSharedKeyValidator>();
        _ = builder.Services.AddSingleton<OrcaWorkerCore.ISlicerProfilesService>(sp =>
            sp.GetRequiredService<OrcaWorkerSvc.CachedOrcaProfilesService>());

        WebApplication app = builder.Build();
        _worker = app;
        _ = app.UseRouting();
        _ = app.MapControllers();
        await app.StartAsync();
        TestServer testServer = app.GetTestServer();
        testServer.BaseAddress = new Uri(WorkerBaseUrl);
        return testServer.CreateHandler();
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
            Name = "E2E Catalog"
        });
        appDb.PrinterModels.Add(new PrinterModel
        {
            Id = targetModelId,
            ManufacturerId = manufacturerId,
            Name = "E2E Target"
        });
        await appDb.SaveChangesAsync();

        SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        slicerDb.SlicerServices.Add(new SlicerService
        {
            Id = Guid.NewGuid(),
            Name = "e2e-worker",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = "2.4.2",
            Host = WorkerBaseUrl,
            Status = "Online",
            LastSeen = DateTime.UtcNow,
            CapabilitiesJson =
                $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
        });
        await slicerDb.SaveChangesAsync();
    }

    /// <summary>
    /// Writes a minimal stock manufacturer bundle that exercises the worker's
    /// real HTTP <c>GetAllProfilesAsync</c> path. That path enumerates
    /// <c>machine_model_list</c> entries (not <c>machine_list</c>), so the
    /// bundle here declares one machine model and one machine, with the
    /// machine model file living at the referenced sub-path so
    /// <c>OrcaProfilesService.ListAvailableMachineModelProfilesAsync</c> picks
    /// it up. The caller writes to both the stock root and the overlay root
    /// so <c>CachedOrcaProfilesService</c> and <c>CustomProfileBundleStore</c>
    /// see the same content without needing filesystem symlinks (which require
    /// elevated privileges on Windows).
    /// </summary>
    private static void WriteStockProfiles(string profilesPath)
    {
        string machinePath = Path.Join(profilesPath, SourceManufacturer, "machine");
        string machineModelPath = Path.Join(
            profilesPath, SourceManufacturer, "machine_model");
        Directory.CreateDirectory(machinePath);
        Directory.CreateDirectory(machineModelPath);
        File.WriteAllText(
            Path.Join(profilesPath, $"{SourceManufacturer}.json"),
            $$"""
            {
              "name": "{{SourceManufacturer}}",
              "version": "01.00.00.00",
              "machine_model_list": [
                {
                  "name": "{{SourceModel}}",
                  "sub_path": "machine_model/{{SourceModel}}.json"
                }
              ],
              "machine_list": [
                {
                  "name": "{{SourceMachine}}",
                  "sub_path": "machine/{{SourceMachine}}.json"
                }
              ],
              "process_list": [],
              "filament_list": []
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(machineModelPath, $"{SourceModel}.json"),
            $$"""
            {
              "type": "machine_model",
              "name": "{{SourceModel}}",
              "model_id": "{{SourceModel}}",
              "nozzle_diameter": "0.4",
              "family": "{{SourceManufacturer}}",
              "instantiation": "true"
            }
            """,
            Encoding.UTF8);
        File.WriteAllText(
            Path.Join(machinePath, $"{SourceMachine}.json"),
            $$"""
            {
              "type": "machine",
              "name": "{{SourceMachine}}",
              "from": "system",
              "instantiation": "true",
              "printer_model": "{{SourceModel}}",
              "nozzle_diameter": ["0.4"],
              "max_layer_height": ["0.32"]
            }
            """,
            Encoding.UTF8);
    }

    /// <summary>
    /// Wraps a <see cref="HttpMessageHandler"/> so the test can record every
    /// request URI + method that the API side sends toward the worker. If a
    /// clone attempt fails, the recorded list surfaces which HTTP call in the
    /// chain (catalog fetch, bundle PUT, or slice-time GET) failed — a
    /// diagnostic the raw 503 body doesn't give you.
    /// </summary>
    private sealed class RecordingDelegatingHandler(
        HttpMessageHandler innerHandler,
        List<string> recorder) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.ToString() ?? "<null>";
            try
            {
                HttpResponseMessage response = await base
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                recorder.Add($"{request.Method} {uri} -> {(int)response.StatusCode}");
                return response;
            }
            catch (Exception ex)
            {
                recorder.Add($"{request.Method} {uri} -> EXCEPTION {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }

    /// <summary>
    /// Extends the shared <see cref="CustomWebApplicationFactory"/> to redirect
    /// both the typed <see cref="System.Net.Http.HttpClient"/> registered for
    /// <c>IProfileFamilyWorkerClient</c> and the general
    /// <see cref="System.Net.Http.HttpClient"/> injected into
    /// <c>ProfilesController</c> to a shared handler that targets the hosted
    /// worker <see cref="TestServer"/>. This is the surgical difference from
    /// <see cref="ProfileFamilyCloneLookupAcceptanceTests"/>'s
    /// <c>AcceptanceFactory</c>, which shortcuts around the client entirely
    /// via <c>InProcessProfileFamilyWorkerClient</c>.
    /// </summary>
    private sealed class E2EFactory(HttpMessageHandler workerHandler) : CustomWebApplicationFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            // Directly replace IProfileFamilyWorkerClient with a fresh instance
            // that uses our recording handler. This is more robust than
            // ConfigurePrimaryHttpMessageHandler on the named client, because
            // it doesn't depend on which name AddHttpClient<TClient,TImpl> uses
            // internally.
            _ = services.RemoveAll<IProfileFamilyWorkerClient>();
            _ = services.AddScoped<IProfileFamilyWorkerClient>(sp =>
                new ProfileFamilyWorkerClient(
                    new HttpClient(workerHandler, disposeHandler: false)
                    {
                        Timeout = TimeSpan.FromMinutes(2)
                    },
                    sp.GetRequiredService<ISlicersService>(),
                    sp.GetRequiredService<IConfiguration>(),
                    sp.GetRequiredService<ILogger<ProfileFamilyWorkerClient>>()));

            services.RemoveAll<HttpClient>();
            services.AddSingleton(new HttpClient(workerHandler, disposeHandler: false)
            {
                BaseAddress = new Uri(WorkerBaseUrl)
            });
        }
    }
}
