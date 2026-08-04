using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Verifies canonical submission behaviour: validated string engines, stored-model identity
/// resolution, exact native profile delivery and owner-scoped correlation uniqueness.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class SliceJobCanonicalSubmissionTests : IAsyncLifetime
{
    private const string MachineProfileJson = """{"type":"machine","printer_model":"Test","nozzle_diameter":["0.4"]}""";
    private const string ProcessProfileJson = """{"type":"process","layer_height":"0.2"}""";
    private const string FilamentProfileJson = """{"type":"filament","filament_type":["PLA"]}""";

    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateWorkerClientAsync(
            workerName: "Canonical Worker",
            username: "canonical-worker",
            email: "canonical@example.com");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact(DisplayName = "Submission serializes the engine as a canonical string name")]
    public async Task Submit_SerializesEngineAsCanonicalString()
    {
        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        HttpResponseMessage status = await _client.GetAsync($"/api/slice/{submitted.JobId}");
        string body = await status.Content.ReadAsStringAsync();

        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created);
        _ = body.Should().Contain("\"slicerEngine\":\"OrcaSlicer\"");
        _ = body.Should().NotContain("\"slicerEngine\":0");
    }

    [Theory(DisplayName = "An unknown engine value is rejected with 400")]
    [InlineData("\"NotASlicer\"")]
    [InlineData("999")]
    [InlineData("-1")]
    public async Task Submit_WithUnknownEngine_ReturnsBadRequest(string engineJson)
    {
        string payload = $$"""
            {
              "userId": "{{Guid.NewGuid()}}",
              "modelFileUrl": "models/test.stl",
              "modelFileName": "test.stl",
              "slicerEngine": {{engineJson}}
            }
            """;
        using StringContent content = new(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.PostAsync("/api/slice", content);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Submission requires either a stored model identity or a stored key")]
    public async Task Submit_WithoutAnyModelReference_ReturnsBadRequest()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadCodeAsync(response)).Should().Be("model_reference_required");
    }

    [Fact(DisplayName = "A stored model is bound by identity and never by the caller-supplied URL")]
    public async Task Submit_WithStoredModel_BindsIdentityAndReplacesCallerUrl()
    {
        (Guid modelId, string sha256) = await AddStoredModelAsync(await GetAuthenticatedUserIdAsync());

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            Model3DId = modelId,
            ModelFileUrl = "http://169.254.169.254/latest/meta-data/",
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        SubmitSliceJobResponse submitted = await response.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);
        _ = job.Model3DId.Should().Be(modelId);
        _ = job.ModelSha256.Should().Be(sha256);
        _ = job.ModelFileUrl.Should().Be($"/api/slice/{job.Id}/model");
        _ = job.ModelFileUrl.Should().NotContain("169.254.169.254");
    }

    [Fact(DisplayName = "A stored model owned by another user is not resolvable")]
    public async Task Submit_WithForeignStoredModel_ReturnsBadRequest()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            Model3DId = modelId,
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadCodeAsync(response)).Should().Be("model_not_found");
    }

    [Fact(DisplayName = "A stored model with no recorded uploader is not resolvable by any caller")]
    public async Task Submit_WithUnattributedStoredModel_ReturnsBadRequest()
    {
        // Fails closed: a NULL UploadedByUserId is not "owned by everyone". Legacy or corrupted
        // rows without a recorded uploader must never be adoptable by an arbitrary caller.
        (Guid modelId, _) = await AddStoredModelAsync(ownerId: null);

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = await GetAuthenticatedUserIdAsync(),
            Model3DId = modelId,
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadCodeAsync(response)).Should().Be("model_not_found");
    }

    [Fact(DisplayName = "A claimed job delivers the exact native profile JSON and its digests")]
    public async Task Claim_DeliversExactNativeProfilesAndHashes()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        (Guid machineId, Guid processId, Guid filamentId) = await AddProfilesAsync(userId);

        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            MachineProfileId = machineId,
            ProcessProfileId = processId,
            FilamentProfileId = filamentId,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();

        _ = claimed.MachineProfileJson.Should().Be(MachineProfileJson);
        _ = claimed.ProcessProfileJson.Should().Be(ProcessProfileJson);
        _ = claimed.FilamentProfileJson.Should().Be(FilamentProfileJson);
        _ = claimed.MachineProfileSha256.Should().Be(Sha256(MachineProfileJson));
        _ = claimed.ProcessProfileSha256.Should().Be(Sha256(ProcessProfileJson));
        _ = claimed.FilamentProfileSha256.Should().Be(Sha256(FilamentProfileJson));
        _ = claimed.SlicerVersion.Should().Be("2.3.1");
    }

    [Fact(DisplayName = "Completion rejects profile digests that differ from what was delivered")]
    public async Task Complete_WithMismatchedProfileDigest_ReturnsBadRequest()
    {
        Guid userId = await GetAuthenticatedUserIdAsync();
        (Guid machineId, Guid processId, Guid filamentId) = await AddProfilesAsync(userId);
        _ = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = userId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            MachineProfileId = machineId,
            ProcessProfileId = processId,
            FilamentProfileId = filamentId,
        });
        WorkerSliceJobResponse claimed = await ClaimAsync();

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/complete")
        {
            Content = JsonContent.Create(new CompleteSliceJobRequest
            {
                PrimaryArtifactId = Guid.NewGuid(),
                MachineProfileSha256 = Sha256("tampered"),
                ProcessProfileSha256 = claimed.ProcessProfileSha256,
                FilamentProfileSha256 = claimed.FilamentProfileSha256,
            }),
        };
        AddLease(message, claimed);
        HttpResponseMessage response = await _client.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadCodeAsync(response)).Should().Be("profile_hash_mismatch");
    }

    [Fact(DisplayName = "Completion records effective profile hashes resolved by the worker")]
    public async Task Complete_WorkerResolvedProfiles_RecordsEffectiveHashes()
    {
        string profileSelectionJson =
            """{"machineProfileName":"Test Machine","processProfileName":"Test Process","filamentProfileName":"Test Filament"}""";
        HttpResponseMessage submit = await _client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = await GetAuthenticatedUserIdAsync(),
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = profileSelectionJson,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());

        WorkerSliceJobResponse claimed = await ClaimAsync();
        _ = claimed.SlicerProfileJson.Should().Be(profileSelectionJson);
        _ = claimed.MachineProfileJson.Should().BeNull();

        byte[] gcode = Encoding.UTF8.GetBytes("; generated by worker\nG28\n");
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(gcode);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/x.gcode");
        form.Add(file, "file", "test.gcode");
        form.Add(new StringContent("gcode"), "kind");
        form.Add(new StringContent(Convert.ToHexString(SHA256.HashData(gcode))), "sha256");
        form.Add(new StringContent(gcode.Length.ToString(CultureInfo.InvariantCulture)), "sizeBytes");
        using HttpRequestMessage uploadMessage = new(
            HttpMethod.Post,
            $"/api/slice/{claimed.Id}/artifacts")
        {
            Content = form,
        };
        AddLease(uploadMessage, claimed);
        HttpResponseMessage upload = await _client.SendAsync(uploadMessage);
        _ = upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        using JsonDocument uploadBody = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        Guid artifactId = uploadBody.RootElement.GetProperty("id").GetGuid();

        string machineHash = Sha256("""{"native":"machine"}""");
        string processHash = Sha256("""{"native":"process"}""");
        string filamentHash = Sha256("""{"native":"filament"}""");
        using HttpRequestMessage completeMessage = new(
            HttpMethod.Post,
            $"/api/slice/{claimed.Id}/complete")
        {
            Content = JsonContent.Create(new CompleteSliceJobRequest
            {
                PrimaryArtifactId = artifactId,
                MachineProfileSha256 = machineHash,
                ProcessProfileSha256 = processHash,
                FilamentProfileSha256 = filamentHash,
            }),
        };
        AddLease(completeMessage, claimed);

        HttpResponseMessage complete = await _client.SendAsync(completeMessage);

        _ = complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob persisted = await db.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == claimed.Id);
        _ = persisted.MachineProfileSha256.Should().Be(machineHash);
        _ = persisted.ProcessProfileSha256.Should().Be(processHash);
        _ = persisted.FilamentProfileSha256.Should().Be(filamentHash);
    }

    [Fact(DisplayName = "Standard-job correlation identifiers remain repeatable")]
    public async Task Submit_StandardJobWithDuplicateCorrelation_AllowsBoth()
    {
        Guid correlationId = Guid.NewGuid();
        SubmitSliceJobRequest request = new()
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            CorrelationId = correlationId,
        };

        HttpResponseMessage first = await _client.PostAsJsonAsync("/api/slice", request);
        HttpResponseMessage second = await _client.PostAsJsonAsync("/api/slice", request);

        _ = first.StatusCode.Should().Be(HttpStatusCode.Created);
        _ = second.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact(DisplayName = "Correlation identifiers are unique within a calibration project")]
    public async Task Submit_CalibrationJobWithDuplicateCorrelation_ReturnsConflict()
    {
        Guid correlationId = Guid.NewGuid();
        SubmitSliceJobRequest request = new()
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            CalibrationProjectId = Guid.NewGuid(),
            CorrelationId = correlationId,
        };

        HttpResponseMessage first = await _client.PostAsJsonAsync("/api/slice", request);
        HttpResponseMessage second = await _client.PostAsJsonAsync("/api/slice", request);

        _ = first.StatusCode.Should().Be(HttpStatusCode.Created);
        _ = second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact(DisplayName = "Jobs without a correlation identifier are unaffected by the uniqueness index")]
    public async Task Submit_WithoutCorrelation_AllowsMultipleJobs()
    {
        SubmitSliceJobRequest request = new()
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        };

        HttpResponseMessage first = await _client.PostAsJsonAsync("/api/slice", request);
        HttpResponseMessage second = await _client.PostAsJsonAsync("/api/slice", request);

        _ = first.StatusCode.Should().Be(HttpStatusCode.Created);
        _ = second.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static void AddLease(HttpRequestMessage message, WorkerSliceJobResponse claimed)
    {
        message.Headers.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
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
        User user = await db.Users.AsNoTracking().FirstAsync(value => value.Username == "canonical-worker");
        return user.Id;
    }

    private async Task<(Guid ModelId, string Sha256)> AddStoredModelAsync(Guid? ownerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IStoragePathService storagePaths = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        string root = storagePaths.GetModelUploadDirectory();
        _ = Directory.CreateDirectory(root);
        string storedName = $"{Guid.NewGuid():N}.stl";
        byte[] bytes = Encoding.UTF8.GetBytes("solid canonical-test-model\nendsolid canonical-test-model\n");
        await File.WriteAllBytesAsync(Path.Combine(root, storedName), bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));

        Model3D model = new()
        {
            Id = Guid.NewGuid(),
            Name = "calibration.stl",
            FileName = storedName,
            FilePath = root,
            FileSizeBytes = bytes.Length,
            FileHash = hash,
            UploadedByUserId = ownerId,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ = db.Models3D.Add(model);
        _ = await db.SaveChangesAsync();
        return (model.Id, hash);
    }

    private async Task<(Guid MachineId, Guid ProcessId, Guid FilamentId)> AddProfilesAsync(Guid ownerId)
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
        FilamentProfile filament = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Filament",
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
        return (machine.Id, process.Id, filament.Id);
    }
}
