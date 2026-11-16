using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Regression coverage for issue #1768's acceptance criterion that worker-side failure detail
/// must be retrievable by an admin without leaking internal paths/filenames to non-admins.
/// <c>Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.MapToPublicStatusResponse</c>
/// always reduces <c>ErrorMessage</c> to a generic "Slicing failed." string for every caller, and
/// only populates the real <c>ErrorDetail</c> (which may contain worker container paths, model
/// filenames, or OrcaSlicer stderr) when <see cref="Farm.Infrastructure.Security.PrintFarmerPermissions.IsFarmAdmin"/>
/// is true for the requesting principal.
/// </summary>
public sealed class SliceJobErrorDetailVisibilityTests : IAsyncLifetime, IDisposable
{
    private const string RealErrorDetail =
        "OrcaSlicer exited with code 1: failed to resolve profile '/data/worker/profiles/FilAr PLA Bronce.json'";

    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Fact(DisplayName = "The owning non-admin user sees a generic error message and a null ErrorDetail")]
    public async Task GetStatus_AsOwningNonAdmin_HidesErrorDetail()
    {
        using HttpClient nonAdminClient = await _factory.CreateOperatorClientAsync("queue", "read", username: "error-detail-owner");
        Guid ownerId = await GetUserIdAsync("error-detail-owner");

        Guid jobId = await SubmitJobAsync(ownerId);
        await FailJobAsync(jobId, RealErrorDetail);

        HttpResponseMessage response = await nonAdminClient.GetAsync($"/api/slice/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        SliceJobStatusResponse status = await response.Content.ReadFromJsonAsync<SliceJobStatusResponse>()
            ?? throw new InvalidOperationException("Missing status response.");

        _ = status.ErrorMessage.Should().Be("Slicing failed.");
        _ = status.ErrorDetail.Should().BeNull("non-admins must never see worker-internal paths or filenames");
    }

    [Fact(DisplayName = "A farm admin sees the real worker-side ErrorDetail even for a job owned by another user")]
    public async Task GetStatus_AsFarmAdmin_ExposesErrorDetail()
    {
        using HttpClient nonAdminClient = await _factory.CreateOperatorClientAsync("queue", "read", username: "error-detail-owner-2");
        Guid ownerId = await GetUserIdAsync("error-detail-owner-2");

        Guid jobId = await SubmitJobAsync(ownerId);
        await FailJobAsync(jobId, RealErrorDetail);

        using HttpClient adminClient = await _factory.CreateAdminClientAsync();

        HttpResponseMessage response = await adminClient.GetAsync($"/api/slice/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        SliceJobStatusResponse status = await response.Content.ReadFromJsonAsync<SliceJobStatusResponse>()
            ?? throw new InvalidOperationException("Missing status response.");

        _ = status.ErrorMessage.Should().Be("Slicing failed.");
        _ = status.ErrorDetail.Should().Be(RealErrorDetail, "admins must be able to diagnose worker failures like issue #1768 without shelling into a worker");
    }

    [Fact(DisplayName = "The worker's real /fail error message is persisted and exposed to a farm admin via errorDetail")]
    public async Task FailAsync_ThroughRealEndpoint_PersistsWorkerMessage_AdminSeesErrorDetail()
    {
        using HttpClient nonAdminClient = await _factory.CreateOperatorClientAsync("queue", "read", username: "error-detail-endpoint-owner");
        Guid ownerId = await GetUserIdAsync("error-detail-endpoint-owner");
        Guid jobId = await SubmitJobAsync(ownerId);

        using HttpClient worker = await _factory.CreateWorkerClientAsync(
            workerName: "Error Detail Endpoint Worker",
            username: "error-detail-endpoint-worker",
            email: "error-detail-endpoint-worker@example.com");
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(worker, jobId);

        HttpResponseMessage failResponse = await SendFailAsync(worker, claimed, RealErrorDetail);
        _ = failResponse.StatusCode.Should().Be(HttpStatusCode.OK, await failResponse.Content.ReadAsStringAsync());

        // The real worker message must land in the domain row, not the hardcoded placeholder that
        // FailAsync used to persist regardless of what the worker reported.
        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        SlicerDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob persisted = await verifyDb.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        _ = persisted.ErrorMessage.Should().Be(RealErrorDetail);

        using HttpClient adminClient = await _factory.CreateAdminClientAsync();
        HttpResponseMessage adminResponse = await adminClient.GetAsync($"/api/slice/{jobId}");
        _ = adminResponse.StatusCode.Should().Be(HttpStatusCode.OK, await adminResponse.Content.ReadAsStringAsync());
        SliceJobStatusResponse adminStatus = await adminResponse.Content.ReadFromJsonAsync<SliceJobStatusResponse>()
            ?? throw new InvalidOperationException("Missing status response.");
        _ = adminStatus.ErrorDetail.Should().Be(RealErrorDetail, "a farm admin must see the real diagnostics reported by the worker");
        _ = adminStatus.ErrorMessage.Should().Be("Slicing failed.");

        HttpResponseMessage nonAdminResponse = await nonAdminClient.GetAsync($"/api/slice/{jobId}");
        _ = nonAdminResponse.StatusCode.Should().Be(HttpStatusCode.OK, await nonAdminResponse.Content.ReadAsStringAsync());
        SliceJobStatusResponse nonAdminStatus = await nonAdminResponse.Content.ReadFromJsonAsync<SliceJobStatusResponse>()
            ?? throw new InvalidOperationException("Missing status response.");
        _ = nonAdminStatus.ErrorMessage.Should().Be("Slicing failed.");
        _ = nonAdminStatus.ErrorDetail.Should().BeNull("non-admins must never see worker-internal paths or filenames");
    }

    [Fact(DisplayName = "The my-jobs projection preserves status mapping and admin-only failure detail")]
    public async Task GetMyJobs_AsOwnerAndAdmin_PreservesStatusMappingAndRedactsErrorDetail()
    {
        const string adminUsername = "my-jobs-status-admin";
        const string ownerUsername = "my-jobs-status-owner";
        using HttpClient adminClient = await _factory.CreateAdminClientAsync(username: adminUsername);
        using HttpClient ownerClient = await _factory.CreateOperatorClientAsync(
            "queue",
            "read",
            username: ownerUsername);
        Guid adminId = await GetUserIdAsync(adminUsername);
        Guid ownerId = await GetUserIdAsync(ownerUsername);
        Guid adminJobId = await SubmitJobAsync(adminId);
        Guid ownerJobId = await SubmitJobAsync(ownerId);
        Guid printerId = Guid.NewGuid();
        DateTime startedAt = new(2026, 11, 1, 12, 30, 0, DateTimeKind.Utc);
        DateTime completedAt = startedAt.AddMinutes(2);

        await SetFailedStatusAsync(adminJobId, printerId, startedAt, completedAt);
        await SetFailedStatusAsync(ownerJobId, printerId, startedAt, completedAt);

        using HttpResponseMessage adminResponse = await adminClient.GetAsync("/api/slice/my-jobs");
        _ = adminResponse.StatusCode.Should().Be(HttpStatusCode.OK, await adminResponse.Content.ReadAsStringAsync());
        List<SliceJobStatusResponse> adminJobs =
            await adminResponse.Content.ReadFromJsonAsync<List<SliceJobStatusResponse>>()
            ?? throw new InvalidOperationException("Missing admin status response.");
        SliceJobStatusResponse adminStatus = adminJobs.Should().ContainSingle().Which;
        AssertStatusProjection(adminStatus, adminJobId, printerId, startedAt, completedAt);
        _ = adminStatus.ErrorDetail.Should().Be(RealErrorDetail);

        using HttpResponseMessage ownerResponse = await ownerClient.GetAsync("/api/slice/my-jobs");
        _ = ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK, await ownerResponse.Content.ReadAsStringAsync());
        List<SliceJobStatusResponse> ownerJobs =
            await ownerResponse.Content.ReadFromJsonAsync<List<SliceJobStatusResponse>>()
            ?? throw new InvalidOperationException("Missing owner status response.");
        SliceJobStatusResponse ownerStatus = ownerJobs.Should().ContainSingle().Which;
        AssertStatusProjection(ownerStatus, ownerJobId, printerId, startedAt, completedAt);
        _ = ownerStatus.ErrorDetail.Should().BeNull();
    }

    private async Task<WorkerSliceJobResponse> ClaimSuccessfullyAsync(HttpClient worker, Guid jobId)
    {
        HttpResponseMessage response = await worker.PostAsJsonAsync(
            "/api/slice/claim",
            new ClaimJobRequest
            {
                WorkerId = Guid.Parse(worker.DefaultRequestHeaders.GetValues(WorkerLeaseHeaders.WorkerId).Single()),
                Capabilities = ["orcaslicer", "orcaslicer-upstream"],
                LeaseDurationSeconds = 300,
            });
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        WorkerSliceJobResponse claimed = await response.Content.ReadFromJsonAsync<WorkerSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing claim response.");
        _ = claimed.Id.Should().Be(jobId, "the only queued job in this test must be the one claimed");
        _ = worker.DefaultRequestHeaders.Remove(WorkerClaimHeaders.ClaimToken);
        worker.DefaultRequestHeaders.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        return claimed;
    }

    private static async Task<HttpResponseMessage> SendFailAsync(HttpClient worker, WorkerSliceJobResponse claimed, string errorMessage)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/fail")
        {
            Content = JsonContent.Create(new FailSliceJobRequest(errorMessage)),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        return await worker.SendAsync(message);
    }

    private async Task<Guid> GetUserIdAsync(string username)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User user = await db.Users.AsNoTracking().FirstAsync(value => value.Username == username);
        return user.Id;
    }

    private async Task<Guid> SubmitJobAsync(Guid ownerId)
    {
        // Submitted through a worker (farm_admin) client purely to reach the endpoint; the job's
        // UserId is then reassigned directly to the intended owner, mirroring the direct-DB
        // mutation pattern already used elsewhere in this test project for post-submission state
        // changes that have no dedicated API surface.
        using HttpClient submittingClient = await _factory.CreateWorkerClientAsync(
            workerName: "Error Detail Worker",
            username: "error-detail-worker",
            email: "error-detail-worker@example.com");

        HttpResponseMessage submit = await submittingClient.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = ownerId,
            ModelFileUrl = "models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        _ = submit.StatusCode.Should().Be(HttpStatusCode.Created, await submit.Content.ReadAsStringAsync());
        SubmitSliceJobResponse submitted = await submit.Content.ReadFromJsonAsync<SubmitSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing submit response.");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.SingleAsync(value => value.Id == submitted.JobId);
        job.UserId = ownerId;
        _ = await db.SaveChangesAsync();

        return job.Id;
    }

    private async Task FailJobAsync(Guid jobId, string errorMessage)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.SingleAsync(value => value.Id == jobId);
        job.Status = SliceJobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTime.UtcNow;
        _ = await db.SaveChangesAsync();
    }

    private async Task SetFailedStatusAsync(Guid jobId, Guid printerId, DateTime startedAt, DateTime completedAt)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.SingleAsync(value => value.Id == jobId);
        job.Status = SliceJobStatus.Failed;
        job.PrinterId = printerId;
        job.ProgressPercent = 100;
        job.StartedAt = startedAt;
        job.CompletedAt = completedAt;
        job.ErrorMessage = RealErrorDetail;
        job.LayoutDegradationReason = LayoutDegradationReason.SourcePlacementFallback.ToString();
        job.FailureReason = SliceFailureReason.ModelOutsideBuildVolume.ToString();
        job.EstimatedPrintTimeSeconds = 120;
        job.FilamentUsedGrams = 3.25m;
        job.ModelFileName = "../../worker/internal-path/part.3mf";
        job.SlicerEngineName = null;
        await db.SaveChangesAsync();
    }

    private static void AssertStatusProjection(
        SliceJobStatusResponse status,
        Guid jobId,
        Guid printerId,
        DateTime startedAt,
        DateTime completedAt)
    {
        _ = status.Id.Should().Be(jobId);
        _ = status.Status.Should().Be(SliceJobStatus.Failed);
        _ = status.PrinterId.Should().Be(printerId);
        _ = status.ProgressPercent.Should().Be(100);
        _ = status.StartedAt.Should().Be(startedAt);
        _ = status.CompletedAt.Should().Be(completedAt);
        _ = status.ErrorMessage.Should().Be("Slicing failed.");
        _ = status.LayoutDegradation.Should().Be(LayoutDegradationReason.SourcePlacementFallback);
        _ = status.FailureReason.Should().Be(SliceFailureReason.ModelOutsideBuildVolume);
        _ = status.FailureHint.Should().Be(SliceFailureHints.For(SliceFailureReason.ModelOutsideBuildVolume));
        _ = status.EstimatedPrintTimeSeconds.Should().Be(120);
        _ = status.FilamentUsedGrams.Should().Be(3.25m);
        _ = status.ModelFileName.Should().Be("part.3mf");
        _ = status.SlicerEngine.Should().Be(SlicerEngineType.OrcaSlicer);
        _ = status.ProgressMessage.Should().BeNull();
        _ = status.WorkerId.Should().BeNull();
        _ = status.ArtifactsRoute.Should().Be($"/api/artifacts/job/{jobId}");
    }
}
