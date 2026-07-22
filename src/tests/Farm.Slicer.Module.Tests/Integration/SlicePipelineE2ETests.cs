using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// End-to-end integration test exercising the full slice pipeline via HTTP:
/// Register worker → Submit job → Verify queued → Claim → Report progress → Upload artifact → Complete → Verify completed → Verify artifact accessible.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SlicePipelineE2ETests(ITestOutputHelper output) : IAsyncLifetime
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

    public async Task DisposeAsync()
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

        // 3. Verify job appears in queue via paginated list
        HttpResponseMessage listResp = await _workerClient.GetAsync("/api/slice?status=Queued&page=1&pageSize=10");
        _ = listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        PagedResult<SliceJobStatusResponse>? pagedJobs = await listResp.Content.ReadFromJsonAsync<PagedResult<SliceJobStatusResponse>>();
        _ = pagedJobs.Should().NotBeNull();
        _ = pagedJobs!.TotalCount.Should().BeGreaterThanOrEqualTo(1);
        _ = pagedJobs.Items.Should().Contain(j => j.Id == submitted.JobId);
        _output.WriteLine($"Queue has {pagedJobs.TotalCount} job(s)");

        // 4. Claim the job as the mock worker
        var claimReq = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = ["orcaslicer"],
            LeaseDurationSeconds = 300,
        };

        HttpResponseMessage claimResp = await _workerClient.PostAsJsonAsync("/api/slice/claim", claimReq);
        _ = claimResp.StatusCode.Should().Be(HttpStatusCode.OK, "Claim should return 200 OK");
        SliceJobStatusResponse? claimed = await claimResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = claimed.Should().NotBeNull();
        _ = claimed!.Id.Should().Be(submitted.JobId);
        _ = claimed.Status.Should().Be("Processing");
        _ = claimed.WorkerId.Should().NotBeNull();
        _output.WriteLine($"Job claimed by worker {claimed.WorkerId}");

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
        _ = midStatus.ProgressMessage.Should().Be("Slicing layers 1-100");

        // 6. Upload a mock G-code artifact
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; G28\n; G1 X0 Y0 Z0.3\n; Mock G-code output");
        using var gcodeContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(gcodeBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        gcodeContent.Add(fileContent, "file", "cube.gcode");

        HttpResponseMessage uploadResp = await _workerClient.PostAsync($"/api/artifacts/{submitted.JobId}", gcodeContent);
        _ = uploadResp.StatusCode.Should().Be(HttpStatusCode.Created, "Artifact upload should return 201");
        ArtifactUploadResponse? uploadedArtifact = await uploadResp.Content.ReadFromJsonAsync<ArtifactUploadResponse>();
        _ = uploadedArtifact.Should().NotBeNull();
        _ = uploadedArtifact!.Id.Should().NotBe(Guid.Empty);
        _output.WriteLine($"Artifact uploaded: {uploadedArtifact.Id}");

        // 7. Complete the job with the artifact
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

        // 8. Verify final job status is Completed
        HttpResponseMessage finalStatusResp = await _workerClient.GetAsync($"/api/slice/{submitted.JobId}");
        _ = finalStatusResp.StatusCode.Should().Be(HttpStatusCode.OK);
        SliceJobStatusResponse? finalStatus = await finalStatusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = finalStatus.Should().NotBeNull();
        _ = finalStatus!.Status.Should().Be("Completed");
        _ = finalStatus.ProgressPercent.Should().Be(100);
        _ = finalStatus.EstimatedPrintTimeSeconds.Should().Be(3600);
        _ = finalStatus.FilamentUsedGrams.Should().Be(25.5m);
        _ = finalStatus.ResultFileUrl.Should().NotBeNullOrEmpty();
        _output.WriteLine($"Final status verified: {finalStatus.Status}, result: {finalStatus.ResultFileUrl}");

        // 9. Verify artifacts are accessible via list endpoint
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
            WorkerId = Guid.NewGuid(),
            Capabilities = ["orcaslicer"],
            LeaseDurationSeconds = 300,
        };
        await _workerClient.PostAsJsonAsync("/api/slice/claim", claimReq);

        // Fail the job
        var failReq = new { ErrorMessage = "Slicer crashed: out of memory" };
        HttpResponseMessage failResp = await _workerClient.PostAsJsonAsync($"/api/slice/{submitted!.JobId}/fail", failReq);
        _ = failResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify it's failed
        HttpResponseMessage failedStatus = await _workerClient.GetAsync($"/api/slice/{submitted.JobId}");
        SliceJobStatusResponse? failedJob = await failedStatus.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = failedJob!.Status.Should().Be("Failed");
        _ = failedJob.ErrorMessage.Should().Contain("out of memory");

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
        public Guid Id { get; init; }
        public Guid JobId { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
