using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Exercises the complete production calibration generation path over HTTP: the authenticated
/// generate-job route, the real repository and service graph, an authenticated worker that claims a
/// lease, downloads the stored model, reports progress, uploads a verified artifact and completes the
/// job, and the promotion of the verified result into the G-code library.
/// </summary>
/// <remarks>
/// Nothing in this test substitutes a generation service, a repository, a storage resolver or the
/// promoter. The only thing it stands in for is the pinned OrcaSlicer process itself, which is covered
/// separately by <see cref="CalibrationPinnedOrcaSmokeTests"/>.
/// </remarks>
public sealed class CalibrationGenerationIntegrationTests : IAsyncLifetime
{
    private static readonly Guid OwnerUserId = new("00000000-0000-0000-0000-000000000001");

    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
        });

    private CalibrationGenerationFixture _fixture = null!;
    private Guid _workerId;
    private Guid _workerServiceId;
    private string _workerKey = null!;

    public async Task InitializeAsync()
    {
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await core.Database.EnsureCreatedAsync();
        }

        _fixture = await CalibrationGenerationSeed.SeedAsync(
            CreateCoreContext,
            CalibrationMethodNames.Temperature,
            OwnerUserId,
            tamperSpecification: false);
        (_workerId, _workerServiceId, _workerKey) = await RegisterAttestedWorkerAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact(DisplayName = "The full production path generates, slices, verifies and promotes over HTTP")]
    public async Task GenerateJob_ThroughRealWorkerRoundTrip_PromotesVerifiedGcode()
    {
        using HttpClient caller = CreateCallerClient();
        using HttpClient worker = CreateWorkerClient();

        // 1. The authenticated caller starts the durable saga.
        HttpResponseMessage accepted = await PostGenerateJobAsync(caller, "integration-0001");
        string acceptedBody = await accepted.Content.ReadAsStringAsync();
        _ = accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, acceptedBody);
        _ = accepted.Headers.Location!.OriginalString.Should()
            .Be($"/api/calibration-orchestrations/{_fixture.OrchestrationId}");

        // 2. The durable saga submits the canonical slice job through the production repository path.
        await AdvanceSagaAsync();
        SliceJob job = await GetSliceJobAsync();
        _ = job.Status.Should().Be(SliceJobStatus.Queued);
        _ = job.CalibrationOrchestrationId.Should().Be(_fixture.OrchestrationId);
        _ = job.Model3DId.Should().NotBeNull();

        // 3. A real registered worker claims the job and receives a lease and the exact profiles.
        WorkerSliceJobResponse claimed = await ClaimAsync(worker);
        _ = claimed.Id.Should().Be(job.Id);
        _ = claimed.SlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = claimed.SlicerDistribution.Should().Be(CalibrationContractConstants.SlicerDistribution);
        _ = claimed.SlicerContainerDigest.Should().Be(CalibrationGenerationSeed.ContainerDigest);
        // The claim delivers the effective documents: the exact upstream baselines with every
        // forbidden command or notes key neutralized, and nothing else changed.
        _ = claimed.MachineProfileJson.Should().Be(
            OrcaEffectiveProfileFactory.Derive(CalibrationGenerationSeed.MachineProfileJson).Json);
        _ = claimed.ProcessProfileJson.Should().Be(
            OrcaEffectiveProfileFactory.Derive(CalibrationGenerationSeed.ProcessProfileJson).Json);
        _ = claimed.FilamentProfileJson.Should().Be(
            OrcaEffectiveProfileFactory.Derive(CalibrationGenerationSeed.FilamentProfileJson).Json);
        _ = claimed.ModelFileUrl.Should().Be($"/api/slice/{job.Id}/model");

        // 4. The worker downloads the stored model over the authenticated route and verifies its hash.
        byte[] modelBytes = await DownloadModelAsync(worker, claimed);
        _ = modelBytes.Should().NotBeEmpty();
        _ = Convert.ToHexString(SHA256.HashData(modelBytes)).Should()
            .Be(job.ModelSha256, "the worker must be able to verify exactly what it downloaded");

        // 5. The worker reports progress, uploads its verified output and completes the job.
        await ReportProgressAsync(worker, claimed, 42);
        Guid artifactId = await UploadArtifactAsync(worker, claimed, SlicedOutput);
        await CompleteAsync(worker, claimed, artifactId);

        // 6. The saga verifies the artifact, composes the annotated program and promotes it.
        await AdvanceSagaAsync();

        CalibrationOrchestrationStatusDto status = await GetStatusAsync(caller);
        _ = status.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Completed));
        _ = status.WorkerId.Should().Be(_workerId);
        _ = status.SourceArtifactId.Should().Be(artifactId);
        _ = status.FinalArtifactId.Should().NotBeNull().And.NotBe(artifactId);
        _ = status.GcodeFileId.Should().NotBeNull();
        _ = status.SpecificationSha256.Should().Be(_fixture.Specification.Sha256);
        _ = status.SlicerContainerDigest.Should().Be(CalibrationGenerationSeed.ContainerDigest);
        _ = status.SlicerBinarySha256.Should().Be(CalibrationGenerationSeed.BinaryDigest);

        await using AppDbContext core = CreateCoreContext();
        GcodeFile promoted = await core.GcodeFiles
            .AsNoTracking()
            .SingleAsync(file => file.Id == status.GcodeFileId!.Value);
        _ = promoted.CalibrationAttemptId.Should().Be(_fixture.AttemptId);
        _ = promoted.CalibrationOrchestrationId.Should().Be(_fixture.OrchestrationId);
        _ = promoted.SpecificationSha256.Should().Be(_fixture.Specification.Sha256);
        _ = promoted.PinnedSlicerVersion.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = promoted.ContentSha256.Should().Be(status.GcodeSha256!.ToUpperInvariant());
        _ = promoted.IsImmutable.Should().BeTrue();
    }

    [Fact(DisplayName = "A worker failure over HTTP leaves a durable, recoverable terminal failure")]
    public async Task GenerateJob_WhenWorkerFailsOverHttp_FailsDurably()
    {
        using HttpClient caller = CreateCallerClient();
        using HttpClient worker = CreateWorkerClient();
        _ = (await PostGenerateJobAsync(caller, "integration-failure")).StatusCode
            .Should().Be(HttpStatusCode.Accepted);
        await AdvanceSagaAsync();
        WorkerSliceJobResponse claimed = await ClaimAsync(worker);

        using HttpRequestMessage failure = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/fail")
        {
            Content = JsonContent.Create(new FailSliceJobRequest("slicer exited with a non-zero status")),
        };
        AddLease(failure, claimed);
        HttpResponseMessage failed = await worker.SendAsync(failure);
        _ = failed.IsSuccessStatusCode.Should().BeTrue(await failed.Content.ReadAsStringAsync());

        await AdvanceSagaAsync();

        CalibrationOrchestrationStatusDto status = await GetStatusAsync(caller);
        _ = status.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = status.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.SliceJobFailed);
        _ = status.GcodeFileId.Should().BeNull();

        // The failure detail stays in the slicer context; the durable status stays redacted.
        string serialized = JsonSerializer.Serialize(status);
        _ = serialized.Should().NotContain("slicer exited");
    }

    [Fact(DisplayName = "Two owners download identical generated geometry under the same real SHA-256")]
    public async Task GenerateJob_ForTwoOwners_DownloadsIdenticalContentUnderRealSha256()
    {
        Guid secondOwnerId = Guid.NewGuid();
        CalibrationGenerationFixture secondFixture = await CalibrationGenerationSeed.SeedAsync(
            CreateCoreContext,
            CalibrationMethodNames.Temperature,
            secondOwnerId,
            tamperSpecification: false);
        using HttpClient firstCaller = CreateCallerClient(OwnerUserId);
        using HttpClient secondCaller = CreateCallerClient(secondOwnerId);
        using HttpClient worker = CreateWorkerClient();

        _ = (await PostGenerateJobAsync(firstCaller, _fixture, "two-owner-first")).StatusCode
            .Should().Be(HttpStatusCode.Accepted);
        await AdvanceSagaAsync(_fixture.OrchestrationId);
        SliceJob firstJob = await GetSliceJobAsync(_fixture.OrchestrationId);
        WorkerSliceJobResponse firstClaim = await ClaimAsync(worker);
        byte[] firstBytes = await DownloadModelAsync(worker, firstClaim);

        _ = (await PostGenerateJobAsync(secondCaller, secondFixture, "two-owner-second")).StatusCode
            .Should().Be(HttpStatusCode.Accepted);
        await AdvanceSagaAsync(secondFixture.OrchestrationId);
        SliceJob secondJob = await GetSliceJobAsync(secondFixture.OrchestrationId);
        WorkerSliceJobResponse secondClaim = await ClaimAsync(worker);
        byte[] secondBytes = await DownloadModelAsync(worker, secondClaim);

        string actualSha256 = Convert.ToHexString(SHA256.HashData(firstBytes));
        _ = firstJob.Model3DId.Should().NotBeNull();
        _ = secondJob.Model3DId.Should().NotBeNull();
        _ = secondJob.Model3DId.Value.Should().NotBe(firstJob.Model3DId.Value);
        _ = firstBytes.Should().Equal(secondBytes);
        _ = firstJob.ModelSha256.Should().MatchRegex("^[A-F0-9]{64}$").And.Be(actualSha256);
        _ = secondJob.ModelSha256.Should().MatchRegex("^[A-F0-9]{64}$").And.Be(actualSha256);
    }

    private const string SlicedOutput =
        ";pinned upstream orcaslicer output\nG28\nG1 X10 Y10 Z0.2 F1200 E1\n";

    private static void AddLease(HttpRequestMessage message, WorkerSliceJobResponse claimed)
    {
        message.Headers.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
    }

    private AppDbContext CreateCoreContext()
    {
        IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The scope owns the context; disposing the context first keeps the caller's using pattern
        // honest while the scope is released with the factory.
        return core;
    }

    private HttpClient CreateCallerClient() => CreateCallerClient(OwnerUserId);

    private HttpClient CreateCallerClient(Guid ownerUserId)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", ownerUserId.ToString());
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            string.Join(
                ',',
                PrintFarmerPermissions.Calibration.Generate,
                PrintFarmerPermissions.Calibration.Read,
                PrintFarmerPermissions.Slicing.Submit,
                PrintFarmerPermissions.Slicing.ReadArtifact,
                PrintFarmerPermissions.Queue.Read));
        return client;
    }

    private HttpClient CreateWorkerClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", OwnerUserId.ToString());
        client.DefaultRequestHeaders.Add(WorkerLeaseHeaders.WorkerKey, _workerKey);
        client.DefaultRequestHeaders.Add(WorkerLeaseHeaders.WorkerId, _workerServiceId.ToString());
        return client;
    }

    private async Task<(Guid WorkerId, Guid ServiceId, string ApiKey)> RegisterAttestedWorkerAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Guid serviceId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        string apiKey = $"registry-issued-{Guid.NewGuid():N}";
        string capabilities = CalibrationGenerationSeed.BuildAttestationJson();

        _ = slicer.SlicerServices.Add(new SlicerService
        {
            Id = serviceId,
            Name = "pinned-orca-service",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = CalibrationContractConstants.SlicerVersion,
            Host = "http://private-worker.internal",
            CapabilitiesJson = capabilities,
            MaxConcurrentJobs = 2,
            Status = WorkerStatus.Online,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = slicer.Workers.Add(new Worker
        {
            Id = workerId,
            ServiceId = serviceId.ToString(),
            Name = "pinned-orca-worker",
            EndpointUrl = "http://private-worker.internal",
            CapabilitiesJson = capabilities,
            Version = CalibrationContractConstants.SlicerVersion,
            ApiKey = apiKey,
            Status = WorkerStatus.Online,
            TotalSlots = 2,
            ActiveJobs = 0,
            LastHeartbeat = DateTime.UtcNow,
            RegisteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await slicer.SaveChangesAsync();
        return (workerId, serviceId, apiKey);
    }

    private async Task<HttpResponseMessage> PostGenerateJobAsync(HttpClient client, string operationId)
        => await PostGenerateJobAsync(client, _fixture, operationId);

    private async Task<HttpResponseMessage> PostGenerateJobAsync(
        HttpClient client,
        CalibrationGenerationFixture fixture,
        string operationId)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"/api/calibration-projects/{fixture.ProjectId}/attempts/{fixture.AttemptId}/generate-job")
        {
            Content = JsonContent.Create(fixture.Request()),
        };
        message.Headers.Add("Idempotency-Key", operationId);
        return await client.SendAsync(message);
    }

    private async Task AdvanceSagaAsync() => await AdvanceSagaAsync(_fixture.OrchestrationId);

    private async Task AdvanceSagaAsync(Guid orchestrationId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ICalibrationGenerationSaga saga = scope.ServiceProvider
            .GetRequiredService<ICalibrationGenerationSaga>();
        _ = await saga.ResumeAsync(orchestrationId, CancellationToken.None);
    }

    private async Task<SliceJob> GetSliceJobAsync() => await GetSliceJobAsync(_fixture.OrchestrationId);

    private async Task<SliceJob> GetSliceJobAsync(Guid orchestrationId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return await slicer.SliceJobs
            .AsNoTracking()
            .SingleAsync(job => job.CalibrationOrchestrationId == orchestrationId);
    }

    private async Task<CalibrationOrchestrationStatusDto> GetStatusAsync(HttpClient caller)
    {
        HttpResponseMessage response = await caller.GetAsync(
            $"/api/calibration-orchestrations/{_fixture.OrchestrationId}");
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<CalibrationOrchestrationStatusDto>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<WorkerSliceJobResponse> ClaimAsync(HttpClient worker)
    {
        HttpResponseMessage response = await worker.PostAsJsonAsync(
            "/api/slice/claim",
            new ClaimJobRequest
            {
                WorkerId = _workerServiceId,
                Capabilities = ["orcaslicer", CalibrationContractConstants.UpstreamSlicerCapability],
                LeaseDurationSeconds = 300,
            });
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<WorkerSliceJobResponse>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<byte[]> DownloadModelAsync(HttpClient worker, WorkerSliceJobResponse claimed)
    {
        using HttpRequestMessage message = new(HttpMethod.Get, $"/api/slice/{claimed.Id}/model");
        AddLease(message, claimed);
        HttpResponseMessage response = await worker.SendAsync(message);
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task ReportProgressAsync(
        HttpClient worker,
        WorkerSliceJobResponse claimed,
        int percent)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/progress")
        {
            Content = JsonContent.Create(new SliceJobProgressUpdateRequest { ProgressPercent = percent }),
        };
        AddLease(message, claimed);
        HttpResponseMessage response = await worker.SendAsync(message);
        _ = response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> UploadArtifactAsync(
        HttpClient worker,
        WorkerSliceJobResponse claimed,
        string gcode)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(gcode);
        using MultipartFormDataContent content = [];
        using ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/x.gcode");
        content.Add(file, "file", "calibration.gcode");
        content.Add(new StringContent(SlicerArtifactKinds.Gcode), "kind");
        content.Add(new StringContent(Convert.ToHexString(SHA256.HashData(bytes))), "sha256");
        content.Add(
            new StringContent(bytes.Length.ToString(CultureInfo.InvariantCulture)),
            "sizeBytes");

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/artifacts")
        {
            Content = content,
        };
        AddLease(message, claimed);
        HttpResponseMessage response = await worker.SendAsync(message);
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task CompleteAsync(
        HttpClient worker,
        WorkerSliceJobResponse claimed,
        Guid artifactId)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/complete")
        {
            Content = JsonContent.Create(new CompleteSliceJobRequest
            {
                PrimaryArtifactId = artifactId,
                MachineProfileSha256 = claimed.MachineProfileSha256,
                ProcessProfileSha256 = claimed.ProcessProfileSha256,
                FilamentProfileSha256 = claimed.FilamentProfileSha256,
            }),
        };
        AddLease(message, claimed);
        HttpResponseMessage response = await worker.SendAsync(message);
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
