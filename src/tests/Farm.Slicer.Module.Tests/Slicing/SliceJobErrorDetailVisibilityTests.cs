using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Slicer.Module.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Regression coverage for issue #1768's acceptance criterion that worker-side failure detail
/// must be retrievable by an admin without leaking internal paths/filenames to non-admins.
/// <see cref="Farm.Slicer.Module.Api.Controllers.Slicing.SliceJobController.MapToPublicStatusResponse"/>
/// always reduces <c>ErrorMessage</c> to a generic "Slicing failed." string for every caller, and
/// only populates the real <c>ErrorDetail</c> (which may contain worker container paths, model
/// filenames, or OrcaSlicer stderr) when <see cref="Farm.Infrastructure.Security.PrintFarmerPermissions.IsFarmAdmin"/>
/// is true for the requesting principal.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class SliceJobErrorDetailVisibilityTests : IAsyncLifetime
{
    private const string RealErrorDetail =
        "OrcaSlicer exited with code 1: failed to resolve profile '/data/worker/profiles/FilAr PLA Bronce.json'";

    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

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
        // UserId is then reassigned directly to the non-admin owner below, mirroring the direct-DB
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
}
