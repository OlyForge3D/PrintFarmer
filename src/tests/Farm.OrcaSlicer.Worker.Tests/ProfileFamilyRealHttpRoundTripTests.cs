using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Controllers;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Discovery tests that drive <see cref="CustomProfilesController"/> over a real
/// HTTP transport (via <see cref="TestServer"/>), verifying that the exact
/// request the API-side <c>ProfileFamilyWorkerClient</c> emits round-trips into
/// the worker's SQLite-backed profile cache such that a subsequent lookup on
/// <c>GET /api/profiles/machine/{printerModel}</c> immediately returns the
/// newly installed machine profile — with no reload, restart, or reconciliation
/// tick required.
/// </summary>
/// <remarks>
/// Regression coverage for issue #2073: worker profile cache not refreshed
/// after cloning/creating a printer family — restart required.
///
/// Existing in-process tests (see <see cref="CachedOrcaProfilesServiceReloadTests"/>)
/// exercise the reload primitives directly against <see cref="CustomProfileBundleStore"/>
/// and <see cref="CachedOrcaProfilesService"/>. They do not go through the
/// controller, routing, model binding, auth filter, JSON serialization, or the
/// `MutateAndReloadProfilesAsync` invocation embedded in the controller. This
/// class closes that gap so a future regression in any of those seams cannot
/// silently reintroduce the "requires restart" bug.
/// </remarks>
public sealed class ProfileFamilyRealHttpRoundTripTests : IAsyncDisposable
{
    private const string SharedKey = "test-shared-key";

    private readonly string _testRoot = Path.Join(
        AppContext.BaseDirectory,
        "test-artifacts",
        $"profile-family-http-{Guid.NewGuid():N}");

    private WebApplication? _app;

    [Fact]
    public async Task InstallCustomBundle_ReturnsInstalledMachineProfile_ImmediatelyOnGet()
    {
        (string stockRoot, string overlayRoot, string customRoot, string dbPath) =
            PrepareRoots();
        WriteStockProfile(stockRoot, overlayRoot);
        HttpClient client = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);

        var familyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string familyName = "IntegrationTestFamily";
        string bundleName = $"PrintFarmer-{familyId:N}";
        object body = BuildFamilyBundleBody(familyId, familyName);

        using HttpRequestMessage installRequest = new(
            HttpMethod.Put,
            $"/api/profiles/custom-bundles/{bundleName}");
        installRequest.Headers.Add(
            RequireWorkerSharedKeyAttribute.HeaderName,
            SharedKey);
        installRequest.Content = JsonContent.Create(body);
        using HttpResponseMessage installResponse =
            await client.SendAsync(installRequest);

        installResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "PUT /api/profiles/custom-bundles/{BundleName} must succeed when the " +
            "rendered bundle inherits from a loaded stock preset — a 4xx or 5xx " +
            "here would prove the bug is in the install or reload seam.");

        using HttpResponseMessage lookupResponse = await client.GetAsync(
            $"/api/profiles/machine/{Uri.EscapeDataString(familyName)}");
        lookupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        List<MachineProfileDto>? profiles =
            await lookupResponse.Content.ReadFromJsonAsync<List<MachineProfileDto>>();

        profiles.Should().NotBeNull(
            "GET /api/profiles/machine/{PrinterModel} must return an array " +
            "immediately after PUT — no restart, reconciliation tick, or " +
            "second reload should be required.");
        profiles!.Should().ContainSingle(
            profile => profile.PrinterModel == familyName,
            "the just-installed machine profile MUST be visible in the SQLite " +
            "cache immediately after MutateAndReloadProfilesAsync — if it is " +
            "not, the reload did not scan the new overlay entries and the bug " +
            "is in the CachedOrcaProfilesService/OrcaProfilesService path.");
    }

    [Fact]
    public async Task InstallCustomBundle_RejectsRequestWithoutSharedKey_With401()
    {
        (string stockRoot, string overlayRoot, string customRoot, string dbPath) =
            PrepareRoots();
        WriteStockProfile(stockRoot, overlayRoot);
        HttpClient client = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);

        var familyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        string bundleName = $"PrintFarmer-{familyId:N}";
        object body = BuildFamilyBundleBody(familyId, "UnauthorizedFamily");

        using HttpRequestMessage installRequest = new(
            HttpMethod.Put,
            $"/api/profiles/custom-bundles/{bundleName}");
        installRequest.Content = JsonContent.Create(body);
        using HttpResponseMessage installResponse =
            await client.SendAsync(installRequest);

        installResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an unauthenticated PUT MUST be rejected before the bundle store " +
            "or reload primitives run; a 4xx-other or 5xx here means the " +
            "RequireWorkerSharedKey filter is misconfigured.");
    }

    [Fact]
    public async Task InstallCustomBundle_RejectsRequestWithWrongSharedKey_With401()
    {
        (string stockRoot, string overlayRoot, string customRoot, string dbPath) =
            PrepareRoots();
        WriteStockProfile(stockRoot, overlayRoot);
        HttpClient client = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);

        var familyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        string bundleName = $"PrintFarmer-{familyId:N}";
        object body = BuildFamilyBundleBody(familyId, "MismatchedKeyFamily");

        using HttpRequestMessage installRequest = new(
            HttpMethod.Put,
            $"/api/profiles/custom-bundles/{bundleName}");
        installRequest.Headers.Add(
            RequireWorkerSharedKeyAttribute.HeaderName,
            "definitely-not-the-right-key");
        installRequest.Content = JsonContent.Create(body);
        using HttpResponseMessage installResponse =
            await client.SendAsync(installRequest);

        installResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InstallCustomBundle_AcceptsAlternateSharedKeyHeader_With200()
    {
        (string stockRoot, string overlayRoot, string customRoot, string dbPath) =
            PrepareRoots();
        WriteStockProfile(stockRoot, overlayRoot);
        HttpClient client = await StartWorkerAsync(
            stockRoot,
            overlayRoot,
            customRoot,
            dbPath);

        var familyId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        const string familyName = "AlternateHeaderFamily";
        string bundleName = $"PrintFarmer-{familyId:N}";
        object body = BuildFamilyBundleBody(familyId, familyName);

        using HttpRequestMessage installRequest = new(
            HttpMethod.Put,
            $"/api/profiles/custom-bundles/{bundleName}");
        installRequest.Headers.Add(
            RequireWorkerSharedKeyAttribute.AlternateHeaderName,
            SharedKey);
        installRequest.Content = JsonContent.Create(body);
        using HttpResponseMessage installResponse =
            await client.SendAsync(installRequest);

        installResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the alternate compact header name (X-Slicer-ApiKey) MUST be " +
            "accepted by RequireWorkerSharedKey — the primary client sends " +
            "X-Slicer-Api-Key, but legacy clients may send the compact form.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    /// <summary>
    /// Builds a request body that mirrors the shape emitted by the API's
    /// <c>ProfileFamilyWorkerClient.WriteBundleAsync</c>: a top-level
    /// manufacturer manifest plus two machine profile files (family base and
    /// one nozzle variant), all inheriting from the seeded stock preset.
    /// </summary>
    private static object BuildFamilyBundleBody(Guid familyId, string familyName)
    {
        string familyIdN = familyId.ToString("N");
        string baseSubPath = $"machine/{familyIdN}/base.json";
        string variantSubPath = $"machine/{familyIdN}/nozzle-0-4.json";
        string baseName = $"{familyName} base";
        string variantName = $"{familyName} 0.4 nozzle";
        return new
        {
            manifest = ParseJson(
                $$"""
                {
                  "name": "Custom",
                  "machine_model_list": [],
                  "machine_list": [
                    {"name":"{{baseName}}","sub_path":"{{baseSubPath}}"},
                    {"name":"{{variantName}}","sub_path":"{{variantSubPath}}"}
                  ],
                  "filament_list": [],
                  "process_list": []
                }
                """),
            files = new object[]
            {
                new
                {
                    relativePath = baseSubPath,
                    familyName,
                    document = ParseJson(
                        $$"""
                        {
                          "name": "{{baseName}}",
                          "inherits": "Stock Parent",
                          "instantiation": "false",
                          "printable_height": "165"
                        }
                        """),
                },
                new
                {
                    relativePath = variantSubPath,
                    familyName,
                    document = ParseJson(
                        $$"""
                        {
                          "name": "{{variantName}}",
                          "inherits": "{{baseName}}",
                          "instantiation": "true",
                          "printer_model": "{{familyName}}",
                          "nozzle_diameter": ["0.4"]
                        }
                        """),
                },
            },
        };
    }

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private (string stockRoot, string overlayRoot, string customRoot, string dbPath)
        PrepareRoots()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "cache", "profiles.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        return (stockRoot, overlayRoot, customRoot, dbPath);
    }

    private async Task<HttpClient> StartWorkerAsync(
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
                ["WorkerAuth:SharedKey"] = SharedKey,
            });

        _ = builder.Services.AddControllers()
            .AddApplicationPart(typeof(CustomProfilesController).Assembly);

        _ = builder.Services.AddSingleton(sp =>
            new CachedOrcaProfilesService(
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<CachedOrcaProfilesService>.Instance,
                profilesPath: overlayRoot,
                dbPath: dbPath,
                customProfilesPath: customRoot));
        _ = builder.Services.AddSingleton(sp =>
            new CustomProfileBundleStore(
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<CustomProfileBundleStore>.Instance,
                stockProfilesPath: stockRoot,
                overlayProfilesPath: overlayRoot,
                customProfilesPath: customRoot));
        _ = builder.Services.AddSingleton<CustomProfilesReconciliationState>();
        _ = builder.Services.AddSingleton<WorkerSharedKeyValidator>();
        _ = builder.Services.AddSingleton<ISlicerProfilesService>(sp =>
            sp.GetRequiredService<CachedOrcaProfilesService>());

        WebApplication app = builder.Build();
        _app = app;
        _ = app.UseRouting();
        _ = app.MapControllers();
        await app.StartAsync();
        return app.GetTestClient();
    }

    private static void WriteStockProfile(
        string stockRoot,
        string overlayRoot)
    {
        string stockDirectory = Path.Join(stockRoot, "Stock", "machine");
        Directory.CreateDirectory(stockDirectory);
        File.WriteAllText(
            Path.Join(stockRoot, "Stock.json"),
            """
            {
              "name": "Stock",
              "machine_model_list": [],
              "machine_list": [
                {
                  "name": "Stock Parent",
                  "sub_path": "machine/Stock Parent.json"
                }
              ],
              "filament_list": [],
              "process_list": []
            }
            """);
        File.WriteAllText(
            Path.Join(stockDirectory, "Stock Parent.json"),
            """
            {
              "name": "Stock Parent",
              "instantiation": "true",
              "printer_model": "Stock Model",
              "nozzle_diameter": ["0.4"],
              "gcode_flavor": "klipper"
            }
            """);
        _ = File.CreateSymbolicLink(
            Path.Join(overlayRoot, "Stock.json"),
            Path.Join(stockRoot, "Stock.json"));
        _ = Directory.CreateSymbolicLink(
            Path.Join(overlayRoot, "Stock"),
            Path.Join(stockRoot, "Stock"));
    }
}
