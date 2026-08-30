using System.Net;
using System.Text.Json;
using Farm.Infrastructure.OrcaSlicer;
using Farm.OrcaSlicer.Worker.Controllers;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Worker.Core;
using Farm.Testing.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests.Contracts;

/// <summary>
/// Native-Orca wire-contract corpus for issue #2238. Drives the REAL production HTTP pipeline —
/// <c>GET /api/profiles/filament</c> served by <c>ProfilesController</c> over a real
/// <see cref="WebApplicationFactory{TEntryPoint}"/>-hosted <c>Farm.OrcaSlicer.Worker</c>, backed
/// by a real <see cref="OrcaProfilesService"/> pointed at genuine, verbatim OrcaSlicer bundle
/// content — and captures the resulting raw <c>snake_case</c> settings bag exactly as it crosses
/// the wire, never a hand-built PrintFarmer DTO field, to
/// <see cref="WireContractCorpusPaths.NativeSlicerRoot"/>.
/// </summary>
/// <remarks>
/// This corpus is DELIBERATELY kept in a directory fully separate from
/// <see cref="WireContractCorpusPaths.ApiRoot"/> and uses no shared normalization/camelCase
/// helper with the PrintFarmer DTO tests, per the issue's explicit instruction: "A SEPARATE
/// native-slicer corpus for Orca snake_case payloads (compatible_printers etc.) — never merged
/// with PrintFarmer DTO fixtures." <see cref="FilamentProfileDto.Settings"/> is populated by
/// <c>OrcaProfilesService</c> from <c>SerializeElementToDict(root)</c> over the fully-resolved
/// (post-<c>inherits</c>-merge) profile JSON, so its keys are the exact native Orca field names
/// (<c>filament_flow_ratio</c>, <c>compatible_printers</c>, etc.) with no naming transformation
/// applied anywhere in this pipeline. Only <see cref="ISlicerProfilesService"/> and the
/// unrelated worker background services are swapped out via
/// <see cref="WebApplicationFactory{TEntryPoint}.ConfigureWebHost"/> — the controller, routing,
/// and JSON serialization pipeline that actually answer the HTTP request are 100% the real
/// production code.
/// </remarks>
public sealed class NativeSlicerCorpusTests : IDisposable
{
    // Verbatim content of the real, vendored OrcaSlicer bundle file checked into this repo at
    // sample_profiles/orcaslicer/Prusa/filament/Prusa Generic PLA.json — copied here (not
    // read from disk at test time) so the fixture is reproducible independent of that file's
    // location and so a future edit to the vendored sample doesn't silently change this corpus
    // without a reviewed test diff.
    private const string RealPrusaGenericPlaFilamentJson = """
        {
        	"type": "filament",
        	"name": "Prusa Generic PLA",
        	"inherits": "fdm_filament_pla",
        	"from": "system",
        	"setting_id": "pKHhR3Hx6AUoyIO3",
        	"instantiation": "true",
        	"filament_flow_ratio": [
        		"0.98"
        	],
        	"filament_max_volumetric_speed": [
        		"12"
        	],
        	"slow_down_layer_time": [
        		"8"
        	],
        	"compatible_printers": [
        		"Prusa MK3S 0.25 nozzle",
        		"Prusa MK3S 0.4 nozzle",
        		"Prusa MK3S 0.6 nozzle",
        		"Prusa MK3S 0.8 nozzle",
        		"Prusa MINI 0.25 nozzle",
        		"Prusa MINI 0.4 nozzle",
        		"Prusa MINI 0.6 nozzle",
        		"Prusa MINI 0.8 nozzle"
        	]
        }
        """;

    private readonly string _profilesRoot;
    private WebApplicationFactory<Program>? _factory;

    public NativeSlicerCorpusTests()
    {
        _profilesRoot = Path.Join(Path.GetTempPath(), "pfarm-native-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    public void Dispose()
    {
        _factory?.Dispose();
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    /// <summary>
    /// Builds the real worker host (real <see cref="ProfilesController"/>, real routing, real
    /// System.Text.Json output pipeline) with only <see cref="ISlicerProfilesService"/> and the
    /// unrelated worker background services swapped so the test host starts instantly against
    /// this test's isolated, per-test <see cref="_profilesRoot"/> directory instead of a real
    /// on-disk OrcaSlicer installation. Must be called only AFTER the bundle/profile files for
    /// the test have been written, because <see cref="OrcaProfilesService"/> is constructed
    /// eagerly here and caches its directory listing.
    /// </summary>
    private HttpClient CreateWorkerClient()
    {
        _factory = new NativeSlicerCorpusApplicationFactory(_profilesRoot);
        return _factory.CreateClient();
    }

    private sealed class NativeSlicerCorpusApplicationFactory(string profilesRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkerAuth:SharedKey"] = "test-registration-key",
                    ["Worker:EngineVersion"] = "test-version",
                    ["Worker:VerifyBinaryVersion"] = "true",
                    // appsettings.json's "/app/temp" is a Docker-deployment path that
                    // doesn't exist (and isn't writable) in the CI test sandbox that hosts
                    // this WebApplicationFactory; every other test in this assembly already
                    // overrides this key for the same reason (see e.g. CalibrationTests,
                    // ProfileHandoffIntegrationTests, WorkerVersionEndpointTests).
                    ["Worker:WorkingDirectory"] = Path.Join(profilesRoot, "work"),
                }));

            _ = builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrcaBinaryDetector>();
                _ = services.AddSingleton<IOrcaBinaryDetector>(new StubBinaryDetector("test-version"));

                // Point the real ProfilesController at this test's isolated profiles
                // directory instead of a real OrcaSlicer installation, without touching
                // process-wide environment variables (which would race with any other
                // test in this assembly running in parallel).
                services.RemoveAll<ISlicerProfilesService>();
                _ = services.AddSingleton<ISlicerProfilesService>(
                    new OrcaProfilesService(NullLogger.Instance, profilesRoot));

                services.RemoveAll<IProfilePreloadService>();
                _ = services.AddSingleton<IProfilePreloadService, NoOpProfilePreloadService>();

                Type[] workerHostedServiceTypes =
                [
                    typeof(GracefulShutdownService),
                    typeof(QueueConsumerService),
                    typeof(RegistrationBackgroundService),
                    typeof(CustomProfilesReconciliationService),
                ];
                ServiceDescriptor[] workerHostedServices = services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType is not null &&
                        workerHostedServiceTypes.Contains(descriptor.ImplementationType))
                    .ToArray();
                foreach (ServiceDescriptor descriptor in workerHostedServices)
                {
                    _ = services.Remove(descriptor);
                }
            });
        }
    }

    private sealed class NoOpProfilePreloadService : IProfilePreloadService
    {
        public Task PreloadProfilesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubBinaryDetector(string version) : IOrcaBinaryDetector
    {
        public bool IsRealBinaryPresent() => true;

        public Task<string?> GetVersionAsync() => Task.FromResult<string?>(version);
    }

    /// <summary>
    /// Populated variant: the real, verbatim Prusa "Generic PLA" filament bundle, parsed by the
    /// real <see cref="OrcaProfilesService"/>. Proves the native <c>compatible_printers</c> array
    /// (a populated collection of 8 printer names) and other snake_case native keys
    /// (<c>filament_flow_ratio</c> etc.) survive real production parsing untouched.
    /// </summary>
    [Fact]
    public async Task GetFilamentProfilesEndpoint_RealPrusaGenericPla_CapturesNativeSnakeCaseSettings()
    {
        WriteManufacturerBundle("Prusa", filamentEntries: [("Prusa Generic PLA", "filament/prusa_generic_pla.json")]);
        WriteProfile("Prusa", "filament/prusa_generic_pla.json", RealPrusaGenericPlaFilamentJson);
        WriteProfile("Prusa", "filament/fdm_filament_pla.json", """
            {
              "type": "filament",
              "name": "fdm_filament_pla",
              "instantiation": "false",
              "filament_type": ["PLA"],
              "temperature": ["210"],
              "bed_temperature": ["60"]
            }
            """);

        using HttpClient client = CreateWorkerClient();
        using HttpResponseMessage response = await client.GetAsync("/api/profiles/filament");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        Dictionary<string, IList<FilamentProfileDto>>? grouped = JsonSerializer.Deserialize<Dictionary<string, IList<FilamentProfileDto>>>(
            responseBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(grouped);
        IList<FilamentProfileDto> profiles = Assert.Single(grouped!.Values);
        FilamentProfileDto profile = Assert.Single(profiles);

        Assert.Equal(8, profile.CompatiblePrinters.Count);
        Assert.Contains("Prusa MK3S 0.4 nozzle", profile.CompatiblePrinters);

        string json = ExtractSingleProfileSettingsRawJson(responseBody);
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            corpusRoot: WireContractCorpusPaths.NativeSlicerRoot,
            relativePath: "filament/prusa-generic-pla.populated.json",
            endpoint: "GET /api/profiles/filament (native settings bag, real HTTP response)",
            producingTest: "Farm.OrcaSlicer.Worker.Tests.Contracts.NativeSlicerCorpusTests.GetFilamentProfilesEndpoint_RealPrusaGenericPla_CapturesNativeSnakeCaseSettings",
            schemaVersion: "1.0",
            actualJson: json);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("compatible_printers", out _),
            "the native settings bag must expose the field under its real snake_case Orca key, never a camelCase-transformed one");
        Assert.True(document.RootElement.TryGetProperty("filament_flow_ratio", out _));
    }

    /// <summary>
    /// Minimal variant: a filament profile with no <c>compatible_printers</c> key at all (legal —
    /// OrcaSlicer profiles commonly rely on <c>compatible_printers_condition</c> instead). Proves
    /// the missing-key case is preserved as a genuinely absent key in the native settings bag,
    /// not defaulted to an empty array within the raw settings dictionary itself (the strongly
    /// typed <see cref="FilamentProfileDto.CompatiblePrinters"/> property does default to an empty
    /// list, but that is a PrintFarmer-side convenience, not a claim about the native payload).
    /// </summary>
    [Fact]
    public async Task GetFilamentProfilesEndpoint_MinimalProfile_OmitsCompatiblePrintersKeyEntirely()
    {
        WriteManufacturerBundle("Acme", filamentEntries: [("Acme Minimal PLA", "filament/minimal.json")]);
        WriteProfile("Acme", "filament/minimal.json", """
            {
              "type": "filament",
              "name": "Acme Minimal PLA",
              "instantiation": "true",
              "filament_type": ["PLA"]
            }
            """);

        using HttpClient client = CreateWorkerClient();
        using HttpResponseMessage response = await client.GetAsync("/api/profiles/filament");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        Dictionary<string, IList<FilamentProfileDto>>? grouped = JsonSerializer.Deserialize<Dictionary<string, IList<FilamentProfileDto>>>(
            responseBody,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(grouped);
        IList<FilamentProfileDto> profiles = Assert.Single(grouped!.Values);
        FilamentProfileDto profile = Assert.Single(profiles);

        Assert.Empty(profile.CompatiblePrinters);

        string json = ExtractSingleProfileSettingsRawJson(responseBody);
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            corpusRoot: WireContractCorpusPaths.NativeSlicerRoot,
            relativePath: "filament/minimal.missing-compatible-printers.json",
            endpoint: "GET /api/profiles/filament (native settings bag, real HTTP response)",
            producingTest: "Farm.OrcaSlicer.Worker.Tests.Contracts.NativeSlicerCorpusTests.GetFilamentProfilesEndpoint_MinimalProfile_OmitsCompatiblePrintersKeyEntirely",
            schemaVersion: "1.0",
            actualJson: json);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("compatible_printers", out _),
            "compatible_printers is genuinely absent from the native profile file — the raw settings bag must not fabricate the key");
    }

    /// <summary>
    /// Extracts the <c>settings</c> sub-element's raw JSON directly from the real HTTP response
    /// body via <see cref="JsonDocument"/> — never through a CLR round-trip. Each test in this
    /// class produces a response grouping a single manufacturer to a single profile, so this
    /// walks <c>{ "&lt;manufacturer&gt;": [ { ..., "settings": {...} } ] }</c> down to that one
    /// <c>settings</c> object and returns its exact bytes as they crossed the wire (via
    /// <see cref="JsonElement.GetRawText"/>), so the captured fixture is provably identical to
    /// what the real MVC/System.Text.Json pipeline emitted — not a value that has been
    /// deserialized into <see cref="FilamentProfileDto.Settings"/> and re-serialized through a
    /// second, locally-constructed <see cref="JsonSerializerOptions"/> that could mask a real
    /// drift in the production options (e.g. a naming policy or converter difference).
    /// </summary>
    private static string ExtractSingleProfileSettingsRawJson(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonProperty manufacturerGroup = Assert.Single(document.RootElement.EnumerateObject());
        JsonElement profileArray = manufacturerGroup.Value;
        JsonElement profileElement = Assert.Single(profileArray.EnumerateArray());
        JsonElement settings = profileElement.GetProperty("settings");
        return settings.GetRawText();
    }

    private void WriteManufacturerBundle(
        string manufacturer,
        (string name, string subPath)[]? machineEntries = null,
        (string name, string subPath)[]? filamentEntries = null,
        (string name, string subPath)[]? processEntries = null)
    {
        string manufacturerDir = Path.Join(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);

        string machineJson = FormatBundleEntries(machineEntries);
        string filamentJson = FormatBundleEntries(filamentEntries);
        string processJson = FormatBundleEntries(processEntries);

        string bundlePath = Path.Join(_profilesRoot, manufacturer + ".json");
        File.WriteAllText(bundlePath, $$"""
            {
              "name": "{{manufacturer}}",
              "version": "1.0",
              "description": "test",
              "machine_model_list": [],
              "machine_list": [{{machineJson}}],
              "filament_list": [{{filamentJson}}],
              "process_list": [{{processJson}}]
            }
            """);
    }

    private static string FormatBundleEntries((string name, string subPath)[]? entries)
    {
        if (entries == null || entries.Length == 0)
        {
            return "";
        }

        return string.Join(",", entries.Select(e =>
            $$"""{"name":"{{e.name}}","sub_path":"{{e.subPath}}"}"""));
    }

    private void WriteProfile(string manufacturer, string subPath, string content)
    {
        string fullPath = Path.Join(_profilesRoot, manufacturer, subPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }
}
