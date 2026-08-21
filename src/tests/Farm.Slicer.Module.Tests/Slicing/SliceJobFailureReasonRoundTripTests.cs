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
/// Regression coverage for issue #1811: a model the slicing engine rejects must reach the API with
/// its diagnostic intact, and the operator who submitted it must be told why without being handed
/// worker internals.
/// </summary>
/// <remarks>
/// <para>
/// Three of five library models failed with <c>OrcaSlicer failed with exit code 156: Errors</c>.
/// The single word "Errors" was all that survived: the worker scraped the console for the first
/// line matching "error"/"fail", and on the engine's slicing-failure path that first line is its own
/// bare <c>ex.what()</c>. The real diagnostic — OrcaSlicer's <c>result.json</c> — was never read.
/// </para>
/// <para>
/// This asserts the whole chain, not just the worker half: the composed detail travels through
/// <c>POST /api/slice/{id}/fail</c> unmodified, and the redacted classification arrives on a
/// separate channel that a non-admin can see. The admin-only visibility contract asserted by
/// <see cref="SliceJobErrorDetailVisibilityTests"/> is deliberately re-asserted here so a future
/// change cannot satisfy "tell the user why" by widening <c>ErrorDetail</c>.
/// </para>
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public sealed class SliceJobFailureReasonRoundTripTests : IAsyncLifetime
{
    /// <summary>
    /// The detail the worker composes for a real exit-156 run, built from the byte-exact console
    /// output and <c>result.json</c> captured from OrcaSlicer 2.4.2 while reproducing issue #1811.
    /// </summary>
    private const string RealWorkerDetail =
        "OrcaSlicer failed with exit code 156 (CLI_SLICING_ERROR, -100): Failed slicing the model. " +
        "Please verify the slicing of all plates on Orca Slicer before uploading. | slicer output: " +
        "Errors; run found error, return -100, exit...";

    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact(DisplayName =
        "A rejected model's diagnostic survives worker → API intact, and is never reduced to \"Errors\"")]
    public async Task FailAsync_WithEngineRejection_PreservesDetailForAdmin()
    {
        using HttpClient owner = await _factory.CreateOperatorClientAsync(
            "queue", "read", username: "failure-reason-owner");
        Guid ownerId = await GetUserIdAsync("failure-reason-owner");
        Guid jobId = await SubmitJobAsync(ownerId);

        using HttpClient worker = await _factory.CreateWorkerClientAsync(
            workerName: "Failure Reason Worker",
            username: "failure-reason-worker",
            email: "failure-reason-worker@example.com");
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(worker, jobId);

        HttpResponseMessage failed = await SendFailAsync(
            worker, claimed, RealWorkerDetail, SliceFailureReason.SlicingEngineRejectedModel);
        _ = failed.StatusCode.Should().Be(HttpStatusCode.OK, await failed.Content.ReadAsStringAsync());

        using HttpClient admin = await _factory.CreateAdminClientAsync();
        SliceJobStatusResponse adminStatus = await GetStatusAsync(admin, jobId);

        _ = adminStatus.ErrorDetail.Should().Be(
            RealWorkerDetail,
            "the composed diagnostic must reach an admin byte-for-byte, not be re-truncated in transit");
        _ = adminStatus.ErrorDetail.Should().Contain("CLI_SLICING_ERROR");
        _ = adminStatus.ErrorDetail.Should().Contain("Failed slicing the model.");
        _ = adminStatus.ErrorDetail.Should().NotBe(
            "OrcaSlicer failed with exit code 156: Errors",
            "that exact string is the issue #1811 regression");
    }

    [Fact(DisplayName =
        "A non-admin is told why the slice failed and what to do, without seeing worker internals")]
    public async Task FailAsync_WithEngineRejection_GivesNonAdminASafeReason()
    {
        using HttpClient owner = await _factory.CreateOperatorClientAsync(
            "queue", "read", username: "failure-reason-owner-2");
        Guid ownerId = await GetUserIdAsync("failure-reason-owner-2");
        Guid jobId = await SubmitJobAsync(ownerId);

        using HttpClient worker = await _factory.CreateWorkerClientAsync(
            workerName: "Failure Reason Worker 2",
            username: "failure-reason-worker-2",
            email: "failure-reason-worker-2@example.com");
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(worker, jobId);
        _ = (await SendFailAsync(
            worker, claimed, RealWorkerDetail, SliceFailureReason.SlicingEngineRejectedModel))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        SliceJobStatusResponse status = await GetStatusAsync(owner, jobId);

        // The pre-existing redaction contract is unchanged.
        _ = status.ErrorMessage.Should().Be("Slicing failed.");
        _ = status.ErrorDetail.Should().BeNull("non-admins must never see worker-internal paths or filenames");

        // ...and the new, structurally-safe channel carries the explanation instead.
        _ = status.FailureReason.Should().Be(SliceFailureReason.SlicingEngineRejectedModel);
        _ = status.FailureHint.Should().NotBeNullOrWhiteSpace();
        _ = status.FailureHint.Should().Be(
            SliceFailureHints.SlicingEngineRejectedModel,
            "the hint must be the fixed constant, never anything derived from the job");
        _ = status.FailureHint.Should().Contain(
            "Auto-orient plate",
            "the operator must be pointed at the control that actually resolves this");
    }

    [Fact(DisplayName = "The client-safe channel cannot carry anything from the worker's diagnostic")]
    public async Task FailAsync_SafeChannel_LeaksNothingFromTheDetail()
    {
        const string LeakyDetail =
            "OrcaSlicer failed with exit code 156 (CLI_SLICING_ERROR, -100): " +
            "/data/worker/jobs/8f2c/top.stl could not be sliced with " +
            "/data/worker/profiles/FilAr PLA Bronce.json";

        using HttpClient owner = await _factory.CreateOperatorClientAsync(
            "queue", "read", username: "failure-reason-owner-3");
        Guid ownerId = await GetUserIdAsync("failure-reason-owner-3");
        Guid jobId = await SubmitJobAsync(ownerId);

        using HttpClient worker = await _factory.CreateWorkerClientAsync(
            workerName: "Failure Reason Worker 3",
            username: "failure-reason-worker-3",
            email: "failure-reason-worker-3@example.com");
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(worker, jobId);
        _ = (await SendFailAsync(
            worker, claimed, LeakyDetail, SliceFailureReason.SlicingEngineRejectedModel))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        SliceJobStatusResponse status = await GetStatusAsync(owner, jobId);

        _ = status.FailureHint.Should().NotBeNull();
        _ = status.FailureHint.Should().NotContain("/data/worker");
        _ = status.FailureHint.Should().NotContain("top.stl");
        _ = status.FailureHint.Should().NotContain("FilAr PLA Bronce");
        _ = status.ErrorDetail.Should().BeNull();
    }

    [Fact(DisplayName = "A worker that reports no reason still fails the job, with no hint invented")]
    public async Task FailAsync_WithoutReason_LeavesTheSafeChannelEmpty()
    {
        using HttpClient owner = await _factory.CreateOperatorClientAsync(
            "queue", "read", username: "failure-reason-owner-4");
        Guid ownerId = await GetUserIdAsync("failure-reason-owner-4");
        Guid jobId = await SubmitJobAsync(ownerId);

        using HttpClient worker = await _factory.CreateWorkerClientAsync(
            workerName: "Failure Reason Worker 4",
            username: "failure-reason-worker-4",
            email: "failure-reason-worker-4@example.com");
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(worker, jobId);

        // Mirrors a worker built before this field existed: FailureReason is simply absent.
        _ = (await SendFailAsync(worker, claimed, RealWorkerDetail, failureReason: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        SliceJobStatusResponse status = await GetStatusAsync(owner, jobId);

        _ = status.ErrorMessage.Should().Be("Slicing failed.");
        _ = status.FailureReason.Should().BeNull();
        _ = status.FailureHint.Should().BeNull("no reason was reported, so no guidance may be fabricated");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob persisted = await db.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        _ = persisted.ErrorMessage.Should().Be(RealWorkerDetail);
        _ = persisted.FailureReason.Should().BeNull();
    }

    private async Task<SliceJobStatusResponse> GetStatusAsync(HttpClient client, Guid jobId)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/slice/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<SliceJobStatusResponse>()
            ?? throw new InvalidOperationException("Missing status response.");
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

    private static async Task<HttpResponseMessage> SendFailAsync(
        HttpClient worker,
        WorkerSliceJobResponse claimed,
        string errorMessage,
        SliceFailureReason? failureReason)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/fail")
        {
            Content = JsonContent.Create(new FailSliceJobRequest(errorMessage, failureReason)),
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
        using HttpClient submittingClient = await _factory.CreateWorkerClientAsync(
            workerName: "Failure Reason Submitter",
            username: "failure-reason-submitter",
            email: "failure-reason-submitter@example.com");

        HttpResponseMessage submit = await submittingClient.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = ownerId,
            ModelFileUrl = "models/top.stl",
            ModelFileName = "top.stl",
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
}
