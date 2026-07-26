using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestData = Farm.Web.Api.Tests.Services.Calibration.Generation.CalibrationGenerationTestData;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Covers the boundary between production model upload storage and the durable calibration generation
/// saga: a model uploaded over <c>POST /api/3d-models/upload</c> must be resolvable by the very
/// <see cref="IModelStorageResolver"/> the saga is injected with, and a final-verification attempt that
/// links it must reach slice submission instead of failing with
/// <see cref="CalibrationGenerationProblemCodes.LinkedAssetMissing"/>.
/// </summary>
/// <remarks>
/// Production upload storage records <c>Model3D.FilePath</c> as the virtual library path ("/") while the
/// bytes are written flat into the model upload root. A resolver that treats that value as a filesystem
/// directory rooted every uploaded model at the filesystem root, pushed it outside the storage root and
/// reported the asset as missing. Every prior generation test seeded the model row itself, so only a run
/// against a real HTTP upload could see it. Nothing here bypasses ownership, injects a model into the
/// resolver or relaxes a failure code: the model is uploaded over the production route and every hop is
/// the production implementation.
/// </remarks>
public sealed class CalibrationUploadedModelGenerationTests : IAsyncLifetime
{
    private static readonly Guid OwnerUserId = new("00000000-0000-0000-0000-000000000001");
    private const string UploadedFileName = "calibration-uploaded-cube.stl";

    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
        });

    private readonly byte[] _fixtureBytes = TestData.BinaryStlCuboid(20f, 20f, 20f);

    private UploadedModel _uploaded = null!;
    private Guid _workerServiceId;
    private string _workerKey = null!;

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await core.Database.EnsureCreatedAsync();
        }

        _uploaded = await UploadModelAsync();
        (_workerServiceId, _workerKey) = await RegisterAttestedWorkerAsync();
    }

    /// <inheritdoc/>
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact(DisplayName = "An uploaded model is readable through the resolver the saga is injected with")]
    public async Task UploadedModel_ResolvesThroughInjectedModelStorageResolver()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IModelStorageResolver resolver = scope.ServiceProvider.GetRequiredService<IModelStorageResolver>();

        Model3D? owned = await resolver.FindOwnedAsync(
            _uploaded.Model3DId,
            OwnerUserId,
            CancellationToken.None);
        _ = owned.Should().NotBeNull("the uploading user owns the stored model");
        _ = owned!.UploadedByUserId.Should().Be(OwnerUserId);

        ModelResolutionResult resolution = await resolver.OpenAsync(
            _uploaded.Model3DId,
            OwnerUserId,
            expectedSha256: null,
            CancellationToken.None);
        _ = resolution.Failure.Should().Be(
            ModelResolutionFailure.None,
            "production upload storage wrote the bytes inside the configured model root");
        _ = resolution.Content.Should().NotBeNull();

        await using Stream content = resolution.Content!.Content;
        using MemoryStream buffer = new();
        await content.CopyToAsync(buffer, CancellationToken.None);
        _ = buffer.ToArray().Should().Equal(
            _fixtureBytes,
            "the resolver must stream exactly the uploaded bytes");
        _ = resolution.Content.SizeBytes.Should().Be(_fixtureBytes.LongLength);
    }

    [Fact(DisplayName = "A final-verification run over an uploaded model submits the slice job")]
    public async Task FinalVerificationRun_WithUploadedModel_ReachesSliceSubmission()
    {
        CalibrationGenerationFixture fixture = await SeedFinalVerificationAsync();
        using HttpClient caller = CreateCallerClient();

        using HttpRequestMessage generate = new(
            HttpMethod.Post,
            $"/api/calibration-projects/{fixture.ProjectId}/attempts/{fixture.AttemptId}/generate-job")
        {
            Content = JsonContent.Create(fixture.Request()),
        };
        generate.Headers.Add("Idempotency-Key", $"uploaded-model-{fixture.OrchestrationId:N}");
        using HttpResponseMessage accepted = await caller.SendAsync(generate);
        _ = accepted.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            await accepted.Content.ReadAsStringAsync());

        await AdvanceSagaAsync(fixture.OrchestrationId);

        CalibrationOrchestrationStatusDto status = await GetStatusAsync(caller, fixture.OrchestrationId);
        _ = status.LastErrorCode.Should().BeNull(
            "the linked asset was uploaded through the production route and must resolve");
        _ = status.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Running));
        _ = status.CurrentStep.Should().Be(CalibrationGenerationSteps.AwaitingWorker);
        _ = status.Model3DId.Should().Be(_uploaded.Model3DId);

        SliceJob job = await ReadSliceJobAsync(fixture.OrchestrationId);
        _ = job.Status.Should().Be(SliceJobStatus.Queued);
        _ = job.Model3DId.Should().Be(_uploaded.Model3DId);
        _ = job.ModelSha256.Should().BeEquivalentTo(_uploaded.Sha256);

        // The worker resolves the same stored bytes through the authenticated model route, which is the
        // second consumer of the storage resolver on this path.
        using HttpClient worker = CreateWorkerClient();
        WorkerSliceJobResponse claimed = await ClaimAsync(worker);
        _ = claimed.Id.Should().Be(job.Id);
        byte[] delivered = await DownloadModelAsync(worker, claimed);
        _ = delivered.Should().Equal(
            _fixtureBytes,
            "the worker must receive exactly the bytes the caller uploaded");
    }

    private async Task<UploadedModel> UploadModelAsync()
    {
        using HttpClient caller = CreateCallerClient();
        using MultipartFormDataContent form = [];
        using ByteArrayContent file = new(_fixtureBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("model/stl");
        form.Add(file, "modelFile", UploadedFileName);

        using HttpResponseMessage response = await caller.PostAsync("/api/3d-models/upload", form);
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        using JsonDocument document = JsonDocument.Parse(body);
        Guid modelId = document.RootElement.GetProperty("id").GetGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Model3D stored = await slicer.Models3D.AsNoTracking().SingleAsync(model => model.Id == modelId);
        _ = stored.UploadedByUserId.Should().Be(OwnerUserId);

        return new UploadedModel(
            modelId,
            Convert.ToHexString(SHA256.HashData(_fixtureBytes)),
            _fixtureBytes.LongLength);
    }

    private Task<CalibrationGenerationFixture> SeedFinalVerificationAsync() =>
        CalibrationGenerationSeed.SeedAsync(
            CreateCoreContext,
            CalibrationMethodNames.FinalVerification,
            OwnerUserId,
            tamperSpecification: false,
            profiles: null,
            new CalibrationModelReference(
                _uploaded.Model3DId,
                _uploaded.Sha256.ToLowerInvariant(),
                CalibrationModelFormats.Stl,
                UploadedFileName,
                _uploaded.SizeBytes,
                "imported"));

    private AppDbContext CreateCoreContext()
    {
        IServiceScope scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    private HttpClient CreateCallerClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", OwnerUserId.ToString());
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

    private async Task<(Guid ServiceId, string ApiKey)> RegisterAttestedWorkerAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Guid serviceId = Guid.NewGuid();
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
            Id = Guid.NewGuid(),
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
        return (serviceId, apiKey);
    }

    private async Task AdvanceSagaAsync(Guid orchestrationId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ICalibrationGenerationSaga saga = scope.ServiceProvider
            .GetRequiredService<ICalibrationGenerationSaga>();
        _ = await saga.ResumeAsync(orchestrationId, CancellationToken.None);
    }

    private async Task<CalibrationOrchestrationStatusDto> GetStatusAsync(
        HttpClient caller,
        Guid orchestrationId)
    {
        HttpResponseMessage response = await caller.GetAsync(
            $"/api/calibration-orchestrations/{orchestrationId}");
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<CalibrationOrchestrationStatusDto>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<SliceJob> ReadSliceJobAsync(Guid orchestrationId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return await slicer.SliceJobs
            .AsNoTracking()
            .SingleAsync(job => job.CalibrationOrchestrationId == orchestrationId);
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
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await worker.SendAsync(message);
        _ = response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>A model that was stored through the production upload route.</summary>
    /// <param name="Model3DId">Stored model identity.</param>
    /// <param name="Sha256">Uppercase hexadecimal SHA-256 of the uploaded bytes.</param>
    /// <param name="SizeBytes">Length of the uploaded bytes.</param>
    private sealed record UploadedModel(Guid Model3DId, string Sha256, long SizeBytes);
}
