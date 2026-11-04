using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Regression coverage for issue #1768: every slice job submitted with a custom/user-owned
/// profile name (rather than a profile ID) used to fail at progressPercent=30 ("Running
/// OrcaSlicer") because the worker's name-based profile lookup only knows its own bundled
/// OrcaSlicer resources and has no database access, so a name like "FilAr PLA Bronce" could
/// never resolve. <see cref="Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController"/>
/// now resolves such names against the database at submission time (system profiles plus the
/// submitting user's own) and snapshots native profile JSON onto the job, routing it onto the
/// same robust <c>NativeProfiles</c> worker path already used by ID-based submissions.
/// </summary>
public sealed class SliceJobNamedProfileResolutionTests : IAsyncLifetime, IDisposable
{
    private const string MachineProfileJson = """{"type":"machine","printer_model":"Test","nozzle_diameter":["0.4"]}""";
    private const string ProcessProfileJson = """{"type":"process","layer_height":"0.2"}""";
    private const string FilamentProfileJson = """{"type":"filament","filament_type":["PLA"]}""";
    private const string CustomFilamentName = "FilAr PLA Bronce";

    private readonly NamedProfileApplicationFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateWorkerClientAsync(
            workerName: "Named Profile Worker",
            username: "named-profile-worker",
            email: "named-profile@example.com");
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact(DisplayName = "A job submitted with a custom filament profile name resolves via the database and reaches a claimable native profile set")]
    public async Task Submit_WithCustomNamedFilamentProfile_ResolvesNativeProfilesFromDatabase()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            overrides = new Dictionary<string, object>(),
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        // Before the fix, the worker could never resolve this name and the job failed almost
        // immediately; here the API must have snapshotted native profile JSON at submission time
        // so the job is claimable with a complete, DB-resolved profile set — regardless of what
        // the worker's own bundled OrcaSlicer resources happen to contain.
        WorkerSliceJobResponse claimed = await ClaimAsync();
        _ = claimed.Id.Should().Be(submitted.JobId);
        _ = claimed.MachineProfileJson.Should().Be(MachineProfileJson);
        _ = claimed.ProcessProfileJson.Should().Be(ProcessProfileJson);
        _ = claimed.FilamentProfileJson.Should().Be(FilamentProfileJson);
        _ = claimed.MachineProfileSha256.Should().Be(Sha256(MachineProfileJson));
        _ = claimed.FilamentProfileSha256.Should().Be(Sha256(FilamentProfileJson));
    }

    [Fact(DisplayName = "Process overrides embedded in SlicerProfileJson are re-applied onto the DB-resolved process profile")]
    public async Task Submit_WithProcessOverride_AppliesOverrideOntoResolvedProcessProfile()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            overrides = new Dictionary<string, object> { ["layer_height"] = "0.28" },
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();

        using JsonDocument process = JsonDocument.Parse(claimed.ProcessProfileJson!);
        _ = process.RootElement.GetProperty("layer_height").GetString().Should().Be("0.28");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Textured PEI Plate")]
    [InlineData("High Temp Plate")]
    public async Task Submit_WithBedSurfaceSelection_PreservesNativeTemperatureVectors(string? selectedBedType)
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);
        const string machineJson = """{"type":"machine","printer_model":"Micron representative","nozzle_diameter":["0.4"],"support_multi_bed_types":"0","curr_bed_type":"Textured PEI Plate"}""";
        const string filamentJson = """{"type":"filament","filament_type":["PLA"],"hot_plate_temp":["0","0"],"hot_plate_temp_initial_layer":["0","0"],"textured_plate_temp":["60","55"],"textured_plate_temp_initial_layer":["65","60"]}""";
        const string processJson = """{"type":"process","layer_height":"0.2","bed_temperature_formula":"by_highest_temp"}""";
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            (await db.MachineProfiles.SingleAsync(p => p.Name == "Test Machine")).RawJson = machineJson;
            (await db.ProcessProfiles.SingleAsync(p => p.Name == "Test Process")).RawJson = processJson;
            (await db.FilamentProfiles.SingleAsync(p => p.Name == CustomFilamentName)).RawJson = filamentJson;
            _ = await db.SaveChangesAsync();
        }

        Dictionary<string, object> overrides = [];
        if (selectedBedType is not null)
        {
            overrides["curr_bed_type"] = selectedBedType;
        }

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = JsonSerializer.Serialize(new
            {
                machineProfileName = "Test Machine",
                processProfileName = "Test Process",
                filamentProfileName = CustomFilamentName,
                overrides,
            }),
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        WorkerSliceJobResponse claimed = await ClaimAsync();
        _ = claimed.MachineProfileJson.Should().Be(machineJson);
        _ = claimed.FilamentProfileJson.Should().Be(filamentJson);
        _ = claimed.FilamentProfileSha256.Should().Be(Sha256(filamentJson));
        using JsonDocument process = JsonDocument.Parse(claimed.ProcessProfileJson!);
        _ = process.RootElement.GetProperty("bed_temperature_formula").GetString().Should().Be("by_highest_temp");
        if (selectedBedType is null)
        {
            _ = process.RootElement.TryGetProperty("curr_bed_type", out _).Should().BeFalse();
        }
        else
        {
            _ = process.RootElement.GetProperty("curr_bed_type").GetString().Should().Be(selectedBedType);
        }

        _ = claimed.ProcessProfileSha256.Should().Be(Sha256(claimed.ProcessProfileJson!));
    }

    [Fact(DisplayName = "A compatibility override is rejected rather than snapshotted onto the native process profile")]
    public async Task Submit_WithCompatibilityOverride_DoesNotSnapshotItOntoTheProcessProfile()
    {
        // Issue #1795. OrcaSlicer gates the slice by comparing each entry of the process
        // document's `compatible_printers` against the machine's system preset name, and the
        // worker materializes `compatible_printers_condition` into that array for profiles that
        // declare only a condition. Both therefore decide whether a pairing is permitted at all —
        // they are not print settings a submission may tune. Accepting either here would let a
        // submission authorize its own machine/process pairing, and this path snapshots a document
        // the worker writes VERBATIM after digest verification, so it would bypass the worker's
        // own filter entirely.
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            overrides = new Dictionary<string, object>
            {
                ["layer_height"] = "0.28",
                ["compatible_printers"] = new[] { "Some Other Printer" },
                ["compatible_printers_condition"] = "name=~/.*/",
            },
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();
        using JsonDocument process = JsonDocument.Parse(claimed.ProcessProfileJson!);

        // The benign override is still applied — overrides remain a supported feature.
        _ = process.RootElement.GetProperty("layer_height").GetString().Should().Be("0.28");

        _ = process.RootElement.TryGetProperty("compatible_printers", out _).Should().BeFalse(
            "a submission must not be able to declare which printers a process profile works with");
        _ = process.RootElement.TryGetProperty("compatible_printers_condition", out _).Should().BeFalse(
            "nor the condition the worker materializes into that declaration");

        // The digest must cover the document actually snapshotted, overrides and all.
        _ = claimed.ProcessProfileSha256.Should().Be(Sha256(claimed.ProcessProfileJson!));
    }

    [Fact(DisplayName = "A numeric process override is stringified to match the native OrcaSlicer JSON schema")]
    public async Task Submit_WithNumericProcessOverride_StringifiesOverrideValue()
    {
        // Regression coverage: OrcaSlicer's CLI parser requires every scalar in process.json to be
        // a JSON string (see HttpJobPollerService's legacy override mapping). A naive
        // JsonNode.Parse(GetRawText()) merge would instead preserve the override's original JS
        // number/boolean type, producing a process.json OrcaSlicer's CLI rejects.
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            overrides = new Dictionary<string, object>
            {
                ["some_numeric_setting"] = 30,
                ["some_boolean_setting"] = true,
            },
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();

        using JsonDocument process = JsonDocument.Parse(claimed.ProcessProfileJson!);
        JsonElement numeric = process.RootElement.GetProperty("some_numeric_setting");
        JsonElement boolean = process.RootElement.GetProperty("some_boolean_setting");
        _ = numeric.ValueKind.Should().Be(JsonValueKind.String, "OrcaSlicer's CLI requires every scalar as a JSON string");
        _ = numeric.GetString().Should().Be("30");
        _ = boolean.ValueKind.Should().Be(JsonValueKind.String);
        _ = boolean.GetString().Should().Be("1");
    }

    [Theory(DisplayName = "Named stock DTO profiles unwrap their native settings, including mixed stock/custom selections")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_WithSystemProfilesStoredAsDtos_SnapshotsNativeSettings(bool stockFilament)
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            MachineProfile machine = await db.MachineProfiles.SingleAsync(m => m.Name == "Test Machine");
            ProcessProfile process = await db.ProcessProfiles.SingleAsync(p => p.Name == "Test Process");
            machine.CreatedByUserId = null;
            machine.IsPublic = true;
            machine.RawJson = WrapSettings(machine.Name, MachineProfileJson);
            process.CreatedByUserId = null;
            process.IsPublic = true;
            process.RawJson = WrapSettings(process.Name, ProcessProfileJson);
            if (stockFilament)
            {
                FilamentProfile filament = await db.FilamentProfiles.SingleAsync(f => f.Name == CustomFilamentName);
                filament.CreatedByUserId = null;
                filament.IsPublic = true;
                filament.RawJson = WrapSettings(filament.Name, FilamentProfileJson);
            }
            _ = await db.SaveChangesAsync();
        }

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            overrides = new Dictionary<string, object> { ["layer_height"] = "0.28" },
            filamentColour = "#123456",
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        WorkerSliceJobResponse claimed = await ClaimAsync();
        _ = claimed.Id.Should().Be(submitted.JobId);
        _ = claimed.MachineProfileJson.Should().Be(MachineProfileJson);
        using JsonDocument processDocument = JsonDocument.Parse(claimed.ProcessProfileJson!);
        using JsonDocument filamentDocument = JsonDocument.Parse(claimed.FilamentProfileJson!);
        _ = processDocument.RootElement.GetProperty("layer_height").GetString().Should().Be("0.28");
        _ = processDocument.RootElement.GetProperty("type").GetString().Should().Be("process");
        _ = filamentDocument.RootElement.GetProperty("filament_colour")[0].GetString().Should().Be("#123456");
        _ = filamentDocument.RootElement.GetProperty("filament_type")[0].GetString().Should().Be("PLA");
        _ = processDocument.RootElement.TryGetProperty("Settings", out _).Should().BeFalse();
        _ = filamentDocument.RootElement.TryGetProperty("Settings", out _).Should().BeFalse();
        _ = claimed.MachineProfileSha256.Should().Be(Sha256(claimed.MachineProfileJson!));
        _ = claimed.ProcessProfileSha256.Should().Be(Sha256(claimed.ProcessProfileJson!));
        _ = claimed.FilamentProfileSha256.Should().Be(Sha256(claimed.FilamentProfileJson!));
    }

    [Theory(DisplayName = "Malformed stock DTO settings do not become native profile snapshots")]
    [InlineData("""{"Name":"Test Machine","Settings":null}""")]
    [InlineData("""{"Name":"Test Machine","Settings":[]}""")]
    [InlineData("""{"Name":"Test Machine","Settings":{"Settings":{"printer_model":"Test"}}}""")]
    public async Task Submit_WithMalformedSystemProfileSettings_DoesNotSnapshotNativeProfiles(string rawJson)
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            MachineProfile machine = await db.MachineProfiles.SingleAsync(m => m.Name == "Test Machine");
            machine.CreatedByUserId = null;
            machine.IsPublic = true;
            machine.RawJson = rawJson;
            _ = await db.SaveChangesAsync();
        }

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        SlicerDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await verifyDb.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);

        _ = job.MachineProfileJson.Should().BeNullOrEmpty();
        _ = job.ProcessProfileJson.Should().BeNullOrEmpty();
        _ = job.FilamentProfileJson.Should().BeNullOrEmpty();
    }

    [Fact(DisplayName = "A profile name that cannot be resolved leaves the job on the legacy worker-side path untouched")]
    public async Task Submit_WithUnresolvableProfileName_DoesNotSnapshotNativeProfiles()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Nonexistent Machine",
            processProfileName = "Nonexistent Process",
            filamentProfileName = "Nonexistent Filament",
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);

        _ = job.MachineProfileJson.Should().BeNullOrEmpty();
        _ = job.ProcessProfileJson.Should().BeNullOrEmpty();
        _ = job.FilamentProfileJson.Should().BeNullOrEmpty();
    }

    [Fact(DisplayName = "When another user publishes profiles with the same names, the caller's own profiles are snapshotted")]
    public async Task Submit_WhenAnotherUsersPublicProfilesShareTheNames_PrefersCallersOwnProfiles()
    {
        // Names are unique per owner, not globally (#3192, #3198), so the caller's own row and
        // another user's public row can share a name. Seed the other user's rows first so an
        // insertion-order pick would choose them.
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddOtherUsersPublicProfilesAsync(Guid.NewGuid(), CustomFilamentName);
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            overrides = new Dictionary<string, object>(),
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();
        _ = claimed.MachineProfileJson.Should().Be(MachineProfileJson);
        _ = claimed.ProcessProfileJson.Should().Be(ProcessProfileJson);
        _ = claimed.FilamentProfileJson.Should().Be(FilamentProfileJson);
    }

    [Fact(DisplayName = "A process profile name shared across incompatible printer models resolves to the one compatible with the selected machine")]
    public async Task Submit_WithDuplicateProcessNameAcrossModels_ResolvesTheCompatibleOne()
    {
        // Regression coverage for a blocking review finding: ProcessProfile.Name is not a unique
        // key (the schema keys on Name + SlicerType + PrinterModelId, and OrcaSlicer commonly
        // ships same-named process profiles scoped to different printer models via
        // CompatiblePrinters). Picking the first name match without regard to the resolved
        // machine could silently snapshot an incompatible profile onto the job.
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        const string compatibleProcessJson = """{"type":"process","layer_height":"0.30"}""";
        const string incompatibleProcessJson = """{"type":"process","layer_height":"0.40"}""";
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            db.ProcessProfiles.AddRange(
                new ProcessProfile
                {
                    Id = Guid.NewGuid(),
                    Name = "Ambiguous Process",
                    SlicerType = SlicerType.OrcaSlicer,
                    SlicerDistribution = "upstream",
                    SlicerVersion = "2.3.1",
                    ProfileFormat = "orca-json",
                    RawJson = compatibleProcessJson,
                    Hash = Sha256(compatibleProcessJson),
                    CompatiblePrinters = "Test Machine",
                    CreatedByUserId = userId,
                    IsPublic = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                },
                new ProcessProfile
                {
                    Id = Guid.NewGuid(),
                    Name = "Ambiguous Process",
                    SlicerType = SlicerType.OrcaSlicer,
                    SlicerDistribution = "upstream",
                    SlicerVersion = "2.3.1",
                    ProfileFormat = "orca-json",
                    RawJson = incompatibleProcessJson,
                    Hash = Sha256(incompatibleProcessJson),
                    CompatiblePrinters = "Some Other Machine",
                    CreatedByUserId = userId,
                    IsPublic = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            _ = await db.SaveChangesAsync();
        }

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Ambiguous Process",
            filamentProfileName = CustomFilamentName,
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();
        _ = claimed.ProcessProfileJson.Should().Be(
            compatibleProcessJson,
            "the profile whose CompatiblePrinters names the selected machine must win over the same-named incompatible one");
    }

    [Fact(DisplayName = "A non-OrcaSlicer submission is never routed through named DB resolution")]
    public async Task Submit_WithNonOrcaEngine_DoesNotSnapshotNativeProfiles()
    {
        // Only OrcaSlicer profiles are mirrored into the database today; PrusaSlicer (or any other
        // engine) must keep using the legacy worker-side name lookup unchanged, even when the
        // submitted profile names happen to also exist as OrcaSlicer database rows.
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.PrusaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);

        _ = job.MachineProfileJson.Should().BeNullOrEmpty();
        _ = job.ProcessProfileJson.Should().BeNullOrEmpty();
        _ = job.FilamentProfileJson.Should().BeNullOrEmpty();
    }

    [Fact(DisplayName = "A multi-extruder submission is left on the legacy per-extruder worker-side resolution")]
    public async Task Submit_WithMultipleExtruderFilamentNames_DoesNotSnapshotNativeProfiles()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            extruderFilamentProfileNames = new[] { CustomFilamentName, CustomFilamentName },
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);

        _ = job.MachineProfileJson.Should().BeNullOrEmpty();
        _ = job.ProcessProfileJson.Should().BeNullOrEmpty();
        _ = job.FilamentProfileJson.Should().BeNullOrEmpty();
    }

    [Fact(DisplayName = "A true partial name match (machine and process resolve, filament does not) leaves the job on the legacy path")]
    public async Task Submit_WithPartiallyResolvableProfileNames_DoesNotSnapshotNativeProfiles()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        string slicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = "Nonexistent Filament",
        });

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = slicerProfileJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);

        _ = job.MachineProfileJson.Should().BeNullOrEmpty();
        _ = job.ProcessProfileJson.Should().BeNullOrEmpty();
        _ = job.FilamentProfileJson.Should().BeNullOrEmpty();
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Submit_MixedWorkerStockAndOwnedProfiles_SnapshotsCompleteSetWithoutImport(
        bool keepMachine, bool keepProcess, bool keepFilament)
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        if (!keepMachine)
        {
            db.MachineProfiles.RemoveRange(await db.MachineProfiles.ToListAsync());
        }
        if (!keepProcess)
        {
            db.ProcessProfiles.RemoveRange(await db.ProcessProfiles.ToListAsync());
        }
        if (!keepFilament)
        {
            db.FilamentProfiles.RemoveRange(await db.FilamentProfiles.ToListAsync());
        }
        await db.SaveChangesAsync();

        AllProfilesResponseDto library = new()
        {
            MachineProfiles = new()
            {
                ["Test"] = [new MachineProfileDto { Name = "Test Machine", Settings = NativeSettings(MachineProfileJson) }],
            },
            ProcessProfiles = new()
            {
                ["Test"] = [new ProcessProfileDto { Name = "Test Process", Settings = NativeSettings(ProcessProfileJson) }],
            },
            FilamentProfiles = new()
            {
                ["Test"] = [new FilamentProfileDto { Name = CustomFilamentName, Settings = NativeSettings(FilamentProfileJson) }],
            },
        };
        // A same-named worker preset must never displace the caller's saved document.
        if (keepProcess)
        {
            library.ProcessProfiles["Test"][0].Settings["layer_height"] = "0.9";
        }
        if (keepFilament)
        {
            library.FilamentProfiles["Test"][0].Settings["filament_type"] = new[] { "ABS" };
        }
        _factory.Profiles.Setup(service => service.GetWorkerProfilesHierarchyAsync(
            It.IsAny<HttpClient>(), It.IsAny<CancellationToken>(), "2.4.2")).ReturnsAsync(library);

        using HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerEngineVersion = "2.4.2",
            SlicerProfileJson = JsonSerializer.Serialize(new
            {
                machineProfileName = "Test Machine",
                processProfileName = "Test Process",
                filamentProfileName = CustomFilamentName,
                overrides = new { layer_height = "0.28" },
                filamentColour = "#123456",
            }),
        });
        submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = (await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>())!;
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);
        using JsonDocument machine = JsonDocument.Parse(job.MachineProfileJson!);
        using JsonDocument process = JsonDocument.Parse(job.ProcessProfileJson!);
        using JsonDocument filament = JsonDocument.Parse(job.FilamentProfileJson!);
        machine.RootElement.GetProperty("printer_model").GetString().Should().Be("Test");
        process.RootElement.GetProperty("layer_height").GetString().Should().Be("0.28");
        filament.RootElement.GetProperty("filament_type")[0].GetString().Should().Be("PLA");
        filament.RootElement.GetProperty("filament_colour")[0].GetString().Should().Be("#123456");
        job.MachineProfileSha256.Should().Be(Sha256(job.MachineProfileJson!));
        job.ProcessProfileSha256.Should().Be(Sha256(job.ProcessProfileJson!));
        job.FilamentProfileSha256.Should().Be(Sha256(job.FilamentProfileJson!));
        (await db.MachineProfiles.CountAsync()).Should().Be(keepMachine ? 1 : 0);
        (await db.ProcessProfiles.CountAsync()).Should().Be(keepProcess ? 1 : 0);
        (await db.FilamentProfiles.CountAsync()).Should().Be(keepFilament ? 1 : 0);
        _factory.Profiles.Verify(service => service.GetWorkerProfilesHierarchyAsync(
            It.IsAny<HttpClient>(), It.IsAny<CancellationToken>(), "2.4.2"), Times.Once);
    }

    [Fact]
    public async Task Submit_MixedProfilesWorkerUnavailable_Returns503WithoutQueuingJob()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, CustomFilamentName);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        db.MachineProfiles.RemoveRange(await db.MachineProfiles.ToListAsync());
        await db.SaveChangesAsync();
        _factory.Profiles.Setup(service => service.GetWorkerProfilesHierarchyAsync(
            It.IsAny<HttpClient>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("Worker offline"));
        using HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = JsonSerializer.Serialize(new
            {
                machineProfileName = "Test Machine",
                processProfileName = "Test Process",
                filamentProfileName = CustomFilamentName,
            }),
        });
        submit.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await db.SliceJobs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Submit_ExplicitSourceWithSameNames_SnapshotsSelectedSource(bool userSource)
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, CustomFilamentName);
        ConfigureExplicitStock();
        SubmitSliceJobRequest request = await ExplicitRequestAsync(userId);
        if (!userSource)
        {
            request.ProfileSelections!.Machine = Stock("Test Machine");
            request.ProfileSelections.Process = Stock("Test Process");
            request.ProfileSelections.Filaments = [Stock(CustomFilamentName)];
        }

        using HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using HttpResponseMessage legacyClaim = await _client.PostAsJsonAsync("/api/slice/claim", new ClaimJobRequest
        {
            WorkerId = Guid.Parse(_client.DefaultRequestHeaders.GetValues(WorkerLeaseHeaders.WorkerId).Single()),
            Capabilities = ["orcaslicer:2.4.2:native-profile-selections-v1"],
            LeaseDurationSeconds = 300,
        });
        legacyClaim.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await RegisterSnapshotCapabilityAsync();
        WorkerSliceJobResponse claimed = await ClaimAsync();
        using JsonDocument machine = JsonDocument.Parse(claimed.MachineProfileJson!);
        using JsonDocument process = JsonDocument.Parse(claimed.ProcessProfileJson!);
        using JsonDocument filament = JsonDocument.Parse(claimed.FilamentProfileJson!);
        machine.RootElement.GetProperty("printer_model").GetString().Should().Be(userSource ? "Test" : "Stock");
        process.RootElement.GetProperty("layer_height").GetString().Should().Be(userSource ? "0.2" : "0.3");
        filament.RootElement.GetProperty("filament_type")[0].GetString().Should().Be(userSource ? "PLA" : "ABS");
        claimed.MachineProfileSha256.Should().Be(Sha256(claimed.MachineProfileJson!));
        claimed.ProcessProfileSha256.Should().Be(Sha256(claimed.ProcessProfileJson!));
        claimed.FilamentProfileSha256.Should().Be(Sha256(claimed.FilamentProfileJson!));
    }

    [Fact]
    public async Task Submit_ExplicitMixedExtruders_PreservesOrderColoursAndOverrides()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, CustomFilamentName);
        ConfigureExplicitStock();
        SubmitSliceJobRequest request = await ExplicitRequestAsync(userId);
        SliceProfileSelection owned = request.ProfileSelections!.Filaments[0];
        request.ProfileSelections.Filaments = [Stock(CustomFilamentName), owned, owned];
        request.ExtruderFilamentProfileNames = [CustomFilamentName, CustomFilamentName, CustomFilamentName];
        request.SlicerProfileJson = JsonSerializer.Serialize(new
        {
            machineProfileName = "Test Machine",
            processProfileName = "Test Process",
            filamentProfileName = CustomFilamentName,
            filamentProfileNames = request.ExtruderFilamentProfileNames,
            filamentColours = new[] { "#112233", "#445566", "#778899" },
            overrides = new { layer_height = "0.28" },
        });

        using HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SliceJob job = await scope.ServiceProvider.GetRequiredService<SlicerDbContext>().SliceJobs.AsNoTracking().SingleAsync();
        using JsonDocument snapshots = JsonDocument.Parse(job.ExtruderFilamentProfilesJson!);
        snapshots.RootElement.GetArrayLength().Should().Be(3);
        job.FilamentProfileSha256.Should().Be(Sha256(string.Join('\0',
            snapshots.RootElement.EnumerateArray().Select(snapshot => snapshot.GetProperty("Json").GetString()))));
        string[] types = ["ABS", "PLA", "PLA"];
        string[] colours = ["#112233", "#445566", "#778899"];
        for (int index = 0; index < 3; index++)
        {
            JsonElement snapshot = snapshots.RootElement[index];
            string json = snapshot.GetProperty("Json").GetString()!;
            using JsonDocument filament = JsonDocument.Parse(json);
            filament.RootElement.GetProperty("filament_type")[0].GetString().Should().Be(types[index]);
            filament.RootElement.GetProperty("filament_colour")[0].GetString().Should().Be(colours[index]);
            snapshot.GetProperty("Sha256").GetString().Should().Be(Sha256(json));
        }
        using JsonDocument process = JsonDocument.Parse(job.ProcessProfileJson!);
        process.RootElement.GetProperty("layer_height").GetString().Should().Be("0.28");
        job.RequiredCapabilitiesJson.Should().Be("""["orcaslicer:2.4.2:native-profile-selections-v1"]""");
        await RegisterSnapshotCapabilityAsync();
        WorkerSliceJobResponse claimed = await ClaimAsync();
        claimed.Id.Should().Be(job.Id);
        claimed.ExtruderFilamentProfilesJson.Should().Be(job.ExtruderFilamentProfilesJson);
        claimed.FilamentProfileSha256.Should().Be(job.FilamentProfileSha256);
        using HttpRequestMessage complete = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/complete")
        {
            Content = JsonContent.Create(new CompleteSliceJobRequest
            {
                PrimaryArtifactId = Guid.NewGuid(),
                MachineProfileSha256 = claimed.MachineProfileSha256,
                ProcessProfileSha256 = claimed.ProcessProfileSha256,
                FilamentProfileSha256 = Sha256(claimed.FilamentProfileJson!),
            }),
        };
        complete.Headers.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        complete.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        complete.Headers.Add(WorkerLeaseHeaders.LeaseFence, claimed.LeaseFence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using HttpResponseMessage completion = await _client.SendAsync(complete);
        completion.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await completion.Content.ReadAsStringAsync()).Should().Contain("profile_hash_mismatch");
    }

    [Theory]
    [InlineData("missing-machine")]
    [InlineData("wrong-type-process")]
    [InlineData("private-filament")]
    [InlineData("renamed-filament")]
    [InlineData("system-id")]
    [InlineData("user-no-id")]
    [InlineData("empty-id")]
    [InlineData("inconsistent-name")]
    [InlineData("inconsistent-order")]
    [InlineData("malformed-native")]
    [InlineData("ambiguous-stock")]
    [InlineData("missing-stock")]
    public async Task Submit_InvalidExplicitIdentity_RejectsWithoutQueuing(string scenario)
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, CustomFilamentName);
        ConfigureExplicitStock();
        SubmitSliceJobRequest request = await ExplicitRequestAsync(userId);
        SliceProfileSelections selections = request.ProfileSelections!;
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        using HttpClient? nonAdminClient = scenario == "private-filament"
            ? await _factory.CreateOperatorClientAsync("slicing", "submit")
            : null;
        switch (scenario)
        {
            case "missing-machine":
                selections.Machine!.CustomProfileId = Guid.NewGuid();
                break;
            case "wrong-type-process":
                selections.Process!.CustomProfileId = selections.Machine!.CustomProfileId;
                break;
            case "private-filament":
                (await db.MachineProfiles.SingleAsync()).IsPublic = true;
                (await db.ProcessProfiles.SingleAsync()).IsPublic = true;
                (await db.FilamentProfiles.SingleAsync()).CreatedByUserId = Guid.NewGuid();
                break;
            case "renamed-filament":
                (await db.FilamentProfiles.SingleAsync()).Name = "Renamed";
                break;
            case "system-id":
                selections.Filaments[0].Source = "system";
                break;
            case "user-no-id":
                selections.Filaments[0].CustomProfileId = null;
                break;
            case "empty-id":
                selections.Filaments[0].CustomProfileId = Guid.Empty;
                break;
            case "inconsistent-name":
                selections.Machine!.Name = "Different";
                break;
            case "inconsistent-order":
                request.ExtruderFilamentProfileNames = ["Different"];
                break;
            case "malformed-native":
                (await db.FilamentProfiles.SingleAsync()).RawJson = "[]";
                break;
            case "ambiguous-stock":
                AllProfilesResponseDto library = ConfigureExplicitStock();
                library.FilamentProfiles["Test"].Add(new FilamentProfileDto
                {
                    Name = CustomFilamentName.ToUpperInvariant(),
                    Settings = NativeSettings(FilamentProfileJson),
                });
                selections.Filaments = [Stock(CustomFilamentName)];
                break;
            case "missing-stock":
                selections.Filaments = [Stock(CustomFilamentName)];
                ConfigureExplicitStock().FilamentProfiles.Clear();
                break;
        }
        await db.SaveChangesAsync();

        using HttpResponseMessage response = await (nonAdminClient ?? _client).PostAsJsonAsync("/api/slice", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_profile_selection");
        (await db.SliceJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Submit_ExplicitStockWorkerUnavailable_DoesNotFallBackToSameNamedOwnedProfile()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, CustomFilamentName);
        SubmitSliceJobRequest request = await ExplicitRequestAsync(userId);
        request.ProfileSelections!.Filaments = [Stock(CustomFilamentName)];
        _factory.Profiles.Setup(service => service.GetWorkerProfilesHierarchyAsync(
            It.IsAny<HttpClient>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new HttpRequestException("Worker offline"));

        using HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", request);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SlicerDbContext>().SliceJobs.CountAsync()).Should().Be(0);
    }

    private static SliceProfileSelection Stock(string name) => new() { Source = "system", Name = name };

    private AllProfilesResponseDto ConfigureExplicitStock()
    {
        AllProfilesResponseDto library = new()
        {
            MachineProfiles = new()
            {
                ["Test"] = [new MachineProfileDto { Name = "Test Machine", Settings = NativeSettings("""{"printer_model":"Stock","nozzle_diameter":["0.4"]}""") }],
            },
            ProcessProfiles = new()
            {
                ["Test"] = [new ProcessProfileDto { Name = "Test Process", Settings = NativeSettings("""{"layer_height":"0.3"}""") }],
            },
            FilamentProfiles = new()
            {
                ["Test"] = [new FilamentProfileDto { Name = CustomFilamentName, Settings = NativeSettings("""{"filament_type":["ABS"]}""") }],
            },
        };
        _factory.Profiles.Setup(service => service.GetWorkerProfilesHierarchyAsync(
            It.IsAny<HttpClient>(), It.IsAny<CancellationToken>(), "2.4.2")).ReturnsAsync(library);
        return library;
    }

    private async Task<SubmitSliceJobRequest> ExplicitRequestAsync(Guid userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerEngineVersion = "2.4.2",
            SlicerProfileJson = JsonSerializer.Serialize(new
            {
                machineProfileName = "Test Machine",
                processProfileName = "Test Process",
                filamentProfileName = CustomFilamentName,
            }),
            ProfileSelections = new()
            {
                Machine = new() { Source = "user", Name = "Test Machine", CustomProfileId = await db.MachineProfiles.Select(p => p.Id).SingleAsync() },
                Process = new() { Source = "user", Name = "Test Process", CustomProfileId = await db.ProcessProfiles.Select(p => p.Id).SingleAsync() },
                Filaments = [new() { Source = "user", Name = CustomFilamentName, CustomProfileId = await db.FilamentProfiles.Select(p => p.Id).SingleAsync() }],
            },
        };
    }

    private static Dictionary<string, object> NativeSettings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

    private sealed class NamedProfileApplicationFactory : CustomWebApplicationFactory
    {
        public Mock<IProfilesService> Profiles { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            Mock<ISlicerLibrary> library = new();
            library.SetupGet(value => value.SlicerName).Returns("OrcaSlicer");
            library.SetupGet(value => value.SlicerVersion).Returns("2.4.2");
            Mock<ISlicerRegistry> registry = new();
            registry.Setup(value => value.GetLibrary("OrcaSlicer", "2.4.2")).Returns(library.Object);
            services.AddSingleton(registry.Object);
            Profiles.Setup(service => service.GetWorkerProfilesHierarchyAsync(
                It.IsAny<HttpClient>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync(new AllProfilesResponseDto());
            services.AddSingleton(Profiles.Object);
        }
    }

    private async Task RegisterSnapshotCapabilityAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker worker = await db.Workers.SingleAsync();
        worker.CapabilitiesJson = """["orcaslicer","orcaslicer:2.4.2:native-profile-selections-v1"]""";
        await db.SaveChangesAsync();
    }

    private async Task<WorkerSliceJobResponse> ClaimAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/slice/claim",
            new ClaimJobRequest
            {
                WorkerId = Guid.Parse(_client.DefaultRequestHeaders.GetValues(WorkerLeaseHeaders.WorkerId).Single()),
                Capabilities = ["orcaslicer", "orcaslicer-upstream"],
                LeaseDurationSeconds = 300,
            });
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<WorkerSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing claim response.");
    }

    private async Task<Guid> GetAuthenticatedUserIdAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User user = await db.Users.AsNoTracking().FirstAsync(value => value.Username == "named-profile-worker");
        return user.Id;
    }

    private async Task AddOtherUsersPublicProfilesAsync(Guid ownerId, string filamentName)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        const string OtherMachineJson = """{"type":"machine","printer_model":"Other","nozzle_diameter":["0.6"]}""";
        const string OtherProcessJson = """{"type":"process","layer_height":"0.32"}""";
        const string OtherFilamentJson = """{"type":"filament","filament_type":["PETG"]}""";

        _ = db.MachineProfiles.Add(new MachineProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Machine",
            SlicerType = SlicerType.OrcaSlicer,
            ProfileFormat = "orca-json",
            RawJson = OtherMachineJson,
            Hash = Sha256(OtherMachineJson),
            CreatedByUserId = ownerId,
            IsPublic = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = db.ProcessProfiles.Add(new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Process",
            SlicerType = SlicerType.OrcaSlicer,
            ProfileFormat = "orca-json",
            RawJson = OtherProcessJson,
            Hash = Sha256(OtherProcessJson),
            CreatedByUserId = ownerId,
            IsPublic = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = db.FilamentProfiles.Add(new FilamentProfile
        {
            Id = Guid.NewGuid(),
            Name = filamentName,
            SlicerType = SlicerType.OrcaSlicer,
            ProfileFormat = "orca-json",
            RawJson = OtherFilamentJson,
            Hash = Sha256(OtherFilamentJson),
            CreatedByUserId = ownerId,
            IsPublic = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await db.SaveChangesAsync();
    }

    private static string WrapSettings(string name, string nativeJson)
    {
        using JsonDocument settings = JsonDocument.Parse(nativeJson);
        return JsonSerializer.Serialize(new { Name = name, Settings = settings.RootElement });
    }

    private async Task AddProfilesAsync(Guid ownerId, string filamentName)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        MachineProfile machine = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Machine",
            SlicerType = SlicerType.OrcaSlicer,
            SlicerDistribution = "upstream",
            SlicerVersion = "2.3.1",
            ProfileFormat = "orca-json",
            RawJson = MachineProfileJson,
            Hash = Sha256(MachineProfileJson),
            CreatedByUserId = ownerId,
            IsPublic = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ProcessProfile process = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Process",
            SlicerType = SlicerType.OrcaSlicer,
            SlicerDistribution = "upstream",
            SlicerVersion = "2.3.1",
            ProfileFormat = "orca-json",
            RawJson = ProcessProfileJson,
            Hash = Sha256(ProcessProfileJson),
            CreatedByUserId = ownerId,
            IsPublic = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        // The custom/user-owned filament profile name that reproduces issue #1768: this name is
        // never bundled with the worker's own OrcaSlicer resources, so only a database-backed
        // resolution path (this fix) can ever succeed for it.
        FilamentProfile filament = new()
        {
            Id = Guid.NewGuid(),
            Name = filamentName,
            SlicerType = SlicerType.OrcaSlicer,
            SlicerDistribution = "upstream",
            SlicerVersion = "2.3.1",
            ProfileFormat = "orca-json",
            RawJson = FilamentProfileJson,
            Hash = Sha256(FilamentProfileJson),
            CreatedByUserId = ownerId,
            IsPublic = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _ = db.MachineProfiles.Add(machine);
        _ = db.ProcessProfiles.Add(process);
        _ = db.FilamentProfiles.Add(filament);
        _ = await db.SaveChangesAsync();
    }
}
