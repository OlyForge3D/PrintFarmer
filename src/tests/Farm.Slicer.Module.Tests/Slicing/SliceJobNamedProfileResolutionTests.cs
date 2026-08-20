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
