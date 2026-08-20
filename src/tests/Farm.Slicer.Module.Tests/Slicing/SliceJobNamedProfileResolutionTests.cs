using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Slicer.Module.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
[Collection(IntegrationTestCollection.Name)]
public sealed class SliceJobNamedProfileResolutionTests : IAsyncLifetime
{
    private const string MachineProfileJson = """{"type":"machine","printer_model":"Test","nozzle_diameter":["0.4"]}""";
    private const string ProcessProfileJson = """{"type":"process","layer_height":"0.2"}""";
    private const string FilamentProfileJson = """{"type":"filament","filament_type":["PLA"]}""";
    private const string CustomFilamentName = "FilAr PLA Bronce";

    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateWorkerClientAsync(
            workerName: "Named Profile Worker",
            username: "named-profile-worker",
            email: "named-profile@example.com");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
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

    [Fact(DisplayName = "A named submission that resolves to a system/stock profile stored as a DTO defers to the legacy worker-side path")]
    public async Task Submit_WithSystemProfileStoredAsDto_DoesNotSnapshotNativeProfiles()
    {
        // Regression coverage for a critical review finding: system/stock profiles imported by
        // SlicersService store RawJson as a serialized CLR DTO (MachineProfileDto/etc, carrying a
        // "Settings" bag), not flat native OrcaSlicer JSON. Snapshotting that DTO verbatim onto
        // NativeProfiles would produce a machine/process/filament.json OrcaSlicer's CLI cannot
        // parse, regressing stock-name submissions that previously worked via the legacy
        // worker-side resolution (which correctly unwraps ".Settings" before writing native JSON).
        // The fix must detect this shape and bail rather than snapshot it.
        Guid userId = await GetAuthenticatedUserIdAsync();
        await AddProfilesAsync(userId, filamentName: CustomFilamentName);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            MachineProfile stockMachine = await db.MachineProfiles.SingleAsync(m => m.Name == "Test Machine");
            // Mirrors SlicersService's system-profile seeding shape: a serialized DTO with promoted
            // properties plus a "Settings" bag, not flat native JSON.
            stockMachine.RawJson = """{"Name":"Test Machine","Settings":{"printer_model":"Test"}}""";
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
