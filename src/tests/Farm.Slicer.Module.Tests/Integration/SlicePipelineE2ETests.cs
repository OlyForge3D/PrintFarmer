using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// End-to-end integration test exercising the full slice pipeline via HTTP:
/// Register worker → Submit job → Verify queued → Claim → Report progress → Upload artifact → Complete → Verify completed → Verify artifact accessible.
/// </summary>
public class SlicePipelineE2ETests(ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();
    private readonly ITestOutputHelper _output = output;
    private HttpClient _workerClient = null!;
    private HttpClient _adminClient = null!;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _workerClient = await _factory.CreateWorkerClientAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _workerClient?.Dispose();
        _adminClient?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "E2E pipeline: submit → claim → progress → complete → verify")]
    public async Task FullPipeline_SubmitThroughCompletion_Succeeds()
    {
        // 1. Verify workers are registered (factory.CreateWorkerClientAsync registers one)
        HttpResponseMessage workersResp = await _workerClient.GetAsync("/api/workers");
        _ = workersResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string workersBody = await workersResp.Content.ReadAsStringAsync();
        _output.WriteLine($"Workers: {workersBody}");

        // 2. Submit a slice job
        var submitReq = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/cube.stl",
            ModelFileName = "cube.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
        };

        HttpResponseMessage submitResp = await _workerClient.PostAsJsonAsync("/api/slice", submitReq);
        _ = submitResp.StatusCode.Should().Be(HttpStatusCode.Created, "Submit should return 201 Created");
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        _ = submitted.Should().NotBeNull();
        _ = submitted!.JobId.Should().NotBe(Guid.Empty);
        _ = submitted.Status.Should().Be("Queued");
        _output.WriteLine($"Submitted job: {submitted.JobId}");

        // 3. Verify job appears in the redacted queue list
        HttpResponseMessage listResp = await _workerClient.GetAsync("/api/slice?status=Queued&limit=10");
        _ = listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        List<SliceJobStatusResponse>? jobs =
            await listResp.Content.ReadFromJsonAsync<List<SliceJobStatusResponse>>();
        _ = jobs.Should().NotBeNull();
        _ = jobs.Should().Contain(j => j.Id == submitted.JobId);
        _output.WriteLine($"Queue has {jobs!.Count} job(s)");

        // 4. Claim the job as the mock worker
        var claimReq = new ClaimJobRequest
        {
            WorkerId = GetWorkerId(),
            Capabilities = ["orcaslicer"],
            LeaseDurationSeconds = 300,
        };

        HttpResponseMessage claimResp = await _workerClient.PostAsJsonAsync("/api/slice/claim", claimReq);
        _ = claimResp.StatusCode.Should().Be(HttpStatusCode.OK, "Claim should return 200 OK");
        WorkerSliceJobResponse? claimed = await claimResp.Content.ReadFromJsonAsync<WorkerSliceJobResponse>();
        _ = claimed.Should().NotBeNull();
        _ = claimed!.Id.Should().Be(submitted.JobId);
        _ = claimed.ClaimToken.Should().NotBe(Guid.Empty);
        _ = claimed.Status.Should().Be("Processing");
        _ = claimed.ModelFileUrl.Should().Be($"/api/slice/{submitted.JobId}/model");
        _output.WriteLine($"Job claimed with protected model route {claimed.ModelFileUrl}");

        // Every subsequent worker mutation must carry the lease the claim issued.
        ApplyLease(claimed);

        // 5. Report progress (50%)
        var progressReq = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 50,
            ProgressMessage = "Slicing layers 1-100",
        };

        HttpResponseMessage progressResp = await _workerClient.PostAsJsonAsync($"/api/slice/{submitted.JobId}/progress", progressReq);
        _ = progressResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _output.WriteLine("Progress reported: 50%");

        // Verify progress was recorded
        HttpResponseMessage statusResp = await _workerClient.GetAsync($"/api/slice/{submitted.JobId}");
        SliceJobStatusResponse? midStatus = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = midStatus!.ProgressPercent.Should().Be(50);
        _ = midStatus.ProgressMessage.Should().Be("Slicing in progress (50%).");

        // 6. Upload a verified G-code artifact
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; G28\n; G1 X0 Y0 Z0.3\n; Mock G-code output");
        string gcodeSha256 = Convert.ToHexString(SHA256.HashData(gcodeBytes));
        using var gcodeContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(gcodeBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        gcodeContent.Add(fileContent, "file", "cube.gcode");
        gcodeContent.Add(new StringContent("gcode"), "kind");
        gcodeContent.Add(new StringContent(gcodeSha256), "sha256");
        gcodeContent.Add(
            new StringContent(gcodeBytes.LongLength.ToString(CultureInfo.InvariantCulture)),
            "sizeBytes");

        HttpResponseMessage uploadResp =
            await _workerClient.PostAsync($"/api/slice/{submitted.JobId}/artifacts", gcodeContent);
        _ = uploadResp.StatusCode.Should().Be(HttpStatusCode.Created, "Artifact upload should return 201");
        ArtifactUploadResponse? uploadedArtifact = await uploadResp.Content.ReadFromJsonAsync<ArtifactUploadResponse>();
        _ = uploadedArtifact.Should().NotBeNull();
        _ = uploadedArtifact!.Id.Should().NotBe(Guid.Empty);
        _ = uploadedArtifact.SizeBytes.Should().BeGreaterThan(0);
        _output.WriteLine($"Artifact uploaded: {uploadedArtifact.Id}");

        // 7. Verify the real server-side artifact bytes and digest.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IArtifactsService artifacts = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
            (Artifact artifact, string fullPath)? stored =
                await artifacts.GetWithPathAsync(uploadedArtifact.Id, default);
            _ = stored.Should().NotBeNull();
            _ = File.Exists(stored!.Value.fullPath).Should().BeTrue();
            byte[] storedBytes = await File.ReadAllBytesAsync(stored.Value.fullPath);
            _ = storedBytes.Should().Equal(gcodeBytes);
            _ = stored.Value.artifact.SizeBytes.Should().Be(gcodeBytes.LongLength);
            _ = stored.Value.artifact.Sha256.Should().Be(gcodeSha256);
            _ = stored.Value.artifact.DeclaredSha256.Should().Be(gcodeSha256);
        }

        // 8. Complete the job with the artifact
        var completeReq = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = uploadedArtifact.Id,
            EstimatedPrintTimeSeconds = 3600,
            FilamentUsedGrams = 25.5m,
        };

        HttpResponseMessage completeResp = await _workerClient.PostAsJsonAsync($"/api/slice/{submitted.JobId}/complete", completeReq);
        _ = completeResp.StatusCode.Should().Be(HttpStatusCode.OK, "Complete should return 200 OK");
        CompleteSliceJobResponse? completed = await completeResp.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();
        _ = completed.Should().NotBeNull();
        _ = completed!.Status.Should().Be("Completed");
        _ = completed.ArtifactIds.Should().Contain(uploadedArtifact.Id);
        _ = completed.EstimatedPrintTimeSeconds.Should().Be(3600);
        _ = completed.FilamentUsedGrams.Should().Be(25.5m);
        _output.WriteLine("Job completed successfully");

        // 9. Verify final job status is Completed
        HttpResponseMessage finalStatusResp = await _workerClient.GetAsync($"/api/slice/{submitted.JobId}");
        _ = finalStatusResp.StatusCode.Should().Be(HttpStatusCode.OK);
        SliceJobStatusResponse? finalStatus = await finalStatusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = finalStatus.Should().NotBeNull();
        _ = finalStatus!.Status.Should().Be("Completed");
        _ = finalStatus.ProgressPercent.Should().Be(100);
        _ = finalStatus.EstimatedPrintTimeSeconds.Should().Be(3600);
        _ = finalStatus.FilamentUsedGrams.Should().Be(25.5m);
        _ = finalStatus.ArtifactsRoute.Should().Be($"/api/artifacts/job/{submitted.JobId}");
        _output.WriteLine($"Final status verified: {finalStatus.Status}");

        // 10. Verify artifacts are accessible via list endpoint
        HttpResponseMessage artifactListResp = await _workerClient.GetAsync($"/api/artifacts/job/{submitted.JobId}");
        _ = artifactListResp.StatusCode.Should().Be(HttpStatusCode.OK);
        string artifactListBody = await artifactListResp.Content.ReadAsStringAsync();
        _ = artifactListBody.Should().Contain(uploadedArtifact.Id.ToString());
        _output.WriteLine($"Artifacts verified: {artifactListBody}");
    }

    [Fact(DisplayName = "E2E retry: failed job can be retried and re-processed")]
    public async Task FailedJob_Retry_Requeues_Successfully()
    {
        // Submit → Claim → Fail → Retry → Verify re-queued
        var submitReq = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/fail.stl",
            ModelFileName = "fail.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
        };

        HttpResponseMessage submitResp = await _workerClient.PostAsJsonAsync("/api/slice", submitReq);
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        // Claim
        var claimReq = new ClaimJobRequest
        {
            WorkerId = GetWorkerId(),
            Capabilities = ["orcaslicer"],
            LeaseDurationSeconds = 300,
        };
        HttpResponseMessage claimResponse = await _workerClient.PostAsJsonAsync("/api/slice/claim", claimReq);
        WorkerSliceJobResponse? claimed = await claimResponse.Content.ReadFromJsonAsync<WorkerSliceJobResponse>();
        _ = claimed.Should().NotBeNull();
        ApplyLease(claimed!);

        // Fail the job
        var failReq = new { ErrorMessage = "Slicer crashed: out of memory" };
        HttpResponseMessage failResp = await _workerClient.PostAsJsonAsync($"/api/slice/{submitted!.JobId}/fail", failReq);
        string failBody = await failResp.Content.ReadAsStringAsync();
        _ = failResp.StatusCode.Should().Be(HttpStatusCode.OK, failBody);

        // Verify it's failed
        HttpResponseMessage failedStatus = await _workerClient.GetAsync($"/api/slice/{submitted.JobId}");
        SliceJobStatusResponse? failedJob = await failedStatus.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = failedJob!.Status.Should().Be("Failed");
        _ = failedJob.ErrorMessage.Should().Be("Slicing failed.");

        // Retry the failed job
        HttpResponseMessage retryResp = await _workerClient.PostAsync($"/api/slice/{submitted.JobId}/retry", null);
        _ = retryResp.StatusCode.Should().Be(HttpStatusCode.OK, "Retry should return 200 OK");
        SliceJobStatusResponse? retried = await retryResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = retried.Should().NotBeNull();
        _ = retried!.Status.Should().Be("Queued");
        _ = retried.ErrorMessage.Should().BeNull();
        _ = retried.ProgressPercent.Should().Be(0);
        _output.WriteLine($"Job retried successfully, new status: {retried.Status}");
    }

    /// <summary>Helper record matching the anonymous type returned by artifact upload.</summary>
    private sealed record ArtifactUploadResponse
    {
        // Id/SizeBytes are populated by JSON deserialization (reflection), which the analyzer
        // cannot see, so it misreports the init accessor as unused even though the getter is
        // read elsewhere in this file.
#pragma warning disable S1144
        public Guid Id { get; init; }
        public long SizeBytes { get; init; }
#pragma warning restore S1144
    }

    private Guid GetWorkerId() =>
        Guid.Parse(_workerClient.DefaultRequestHeaders.GetValues("X-Worker-Id").Single());

    /// <summary>
    /// Binds the worker client to the lease a successful claim issued. Every mutating worker route
    /// is lease-fenced, so the headers must accompany progress, artifact, completion and failure
    /// reports for the rest of the job's life.
    /// </summary>
    /// <param name="claimed">The claim response carrying the issued lease.</param>
    private void ApplyLease(WorkerSliceJobResponse claimed)
    {
        _ = _workerClient.DefaultRequestHeaders.Remove(WorkerClaimHeaders.ClaimToken);
        _ = _workerClient.DefaultRequestHeaders.Remove(WorkerLeaseHeaders.LeaseToken);
        _ = _workerClient.DefaultRequestHeaders.Remove(WorkerLeaseHeaders.LeaseFence);
        _workerClient.DefaultRequestHeaders.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        _workerClient.DefaultRequestHeaders.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        _workerClient.DefaultRequestHeaders.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
    }
}
