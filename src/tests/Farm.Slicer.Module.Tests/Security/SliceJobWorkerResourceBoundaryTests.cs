using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests.Security;

public sealed class SliceJobWorkerResourceBoundaryTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient _firstWorkerClient = null!;
    private HttpClient _secondWorkerClient = null!;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _firstWorkerClient = await _factory.CreateWorkerClientAsync(
            workerName: "First Worker",
            username: "first-worker-user",
            email: "first-worker@example.com");
        _secondWorkerClient = await _factory.CreateWorkerClientAsync(
            workerName: "Second Worker",
            username: "second-worker-user",
            email: "second-worker@example.com");
    }

    public async Task DisposeAsync()
    {
        _firstWorkerClient.Dispose();
        _secondWorkerClient.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ClaimAsync_ServiceIdentityDoesNotMatchRequest_ReturnsUnauthorized()
    {
        Guid secondServiceId = GetServiceId(_secondWorkerClient);
        ClaimJobRequest request = new()
        {
            WorkerId = secondServiceId,
            Capabilities = ["orcaslicer", "orcaslicer-upstream"],
            LeaseDurationSeconds = 300,
        };

        HttpResponseMessage response = await _firstWorkerClient.PostAsJsonAsync("/api/slice/claim", request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Fact]
    public async Task ClaimAsync_ServiceKeyDoesNotMatchIdentity_ReturnsUnauthorized()
    {
        Guid secondServiceId = GetServiceId(_secondWorkerClient);
        string firstServiceKey = _firstWorkerClient.DefaultRequestHeaders
            .GetValues("X-Worker-Key")
            .Single();
        ClaimJobRequest request = new()
        {
            WorkerId = secondServiceId,
            Capabilities = ["orcaslicer", "orcaslicer-upstream"],
            LeaseDurationSeconds = 300,
        };
        using HttpRequestMessage requestMessage = new(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request),
        };
        requestMessage.Headers.Add("X-Worker-Key", firstServiceKey);
        requestMessage.Headers.Add("X-Worker-Id", secondServiceId.ToString());
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(requestMessage);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Fact]
    public async Task ProgressAsync_DifferentWorkerOwnsJob_ReturnsResourceForbidden()
    {
        Worker firstWorker = await GetWorkerAsync(_firstWorkerClient);
        SliceJob job = await AddProcessingJobAsync(firstWorker);
        SliceJobProgressUpdateRequest request = new()
        {
            ProgressPercent = 80,
            ProgressMessage = @"Reading D:\private\models\secret.stl",
        };

        using HttpRequestMessage message = CreateLeasedRequest(
            HttpMethod.Post,
            $"/api/slice/{job.Id}/progress",
            job);
        message.Content = JsonContent.Create(request);
        HttpResponseMessage response = await _secondWorkerClient.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob unchanged = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == job.Id);
        _ = unchanged.ProgressPercent.Should().Be(0);
        _ = unchanged.ProgressMessage.Should().BeNull();
    }

    [Fact]
    public async Task DownloadWorkerModelAsync_OwningWorkerReturnsModel_OtherWorkerIsForbidden()
    {
        Worker firstWorker = await GetWorkerAsync(_firstWorkerClient);
        byte[] modelBytes = Encoding.UTF8.GetBytes("solid private-test-model");
        SliceJob job = await AddProcessingJobAsync(firstWorker, modelBytes);

        using HttpRequestMessage forbiddenMessage = CreateLeasedRequest(
            HttpMethod.Get,
            $"/api/slice/{job.Id}/model",
            job);
        HttpResponseMessage forbidden = await _secondWorkerClient.SendAsync(forbiddenMessage);
        using HttpRequestMessage allowedMessage = CreateLeasedRequest(
            HttpMethod.Get,
            $"/api/slice/{job.Id}/model",
            job);
        HttpResponseMessage allowed = await _firstWorkerClient.SendAsync(allowedMessage);

        _ = forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await allowed.Content.ReadAsByteArrayAsync()).Should().Equal(modelBytes);
        _ = allowed.Content.Headers.ContentDisposition?.FileNameStar.Should().Be("model.stl");
        _ = allowed.RequestMessage!.RequestUri!.ToString().Should().NotContain("file:");
    }

    [Fact]
    public async Task UploadWorkerArtifactAsync_OwningWorkerCreatesRedactedArtifact_OtherWorkerIsForbidden()
    {
        Worker firstWorker = await GetWorkerAsync(_firstWorkerClient);
        SliceJob job = await AddProcessingJobAsync(firstWorker);

        using HttpRequestMessage forbiddenMessage = CreateLeasedRequest(
            HttpMethod.Post,
            $"/api/slice/{job.Id}/artifacts",
            job);
        forbiddenMessage.Content = CreateGcodeUpload();
        HttpResponseMessage forbidden = await _secondWorkerClient.SendAsync(forbiddenMessage);
        using HttpRequestMessage allowedMessage = CreateLeasedRequest(
            HttpMethod.Post,
            $"/api/slice/{job.Id}/artifacts",
            job);
        allowedMessage.Content = CreateGcodeUpload();
        HttpResponseMessage allowed = await _firstWorkerClient.SendAsync(allowedMessage);
        string body = await allowed.Content.ReadAsStringAsync();
        string normalizedBody = body.ToLowerInvariant();

        _ = forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = allowed.StatusCode.Should().Be(HttpStatusCode.Created, body);
        _ = normalizedBody.Should().NotContain("relativepath");
        _ = normalizedBody.Should().NotContain("sha256");
        _ = normalizedBody.Should().NotContain("workerid");
    }

    [Fact]
    public async Task CompleteAsync_ArtifactOwnedByDifferentWorker_ReturnsBadRequest()
    {
        Worker firstWorker = await GetWorkerAsync(_firstWorkerClient);
        Worker secondWorker = await GetWorkerAsync(_secondWorkerClient);
        SliceJob job = await AddProcessingJobAsync(firstWorker);
        Artifact foreignArtifact = new()
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            WorkerId = secondWorker.Id,
            Kind = "gcode",
            FileName = "foreign.gcode",
            RelativePath = "private/foreign.gcode",
            ContentType = "text/x.gcode",
            SizeBytes = 10,
            Sha256 = "not-public",
            CreatedAt = DateTime.UtcNow,
        };
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            _ = db.Artifacts.Add(foreignArtifact);
            _ = await db.SaveChangesAsync();
        }

        CompleteSliceJobRequest request = new() { PrimaryArtifactId = foreignArtifact.Id };
        using HttpRequestMessage message = CreateLeasedRequest(
            HttpMethod.Post,
            $"/api/slice/{job.Id}/complete",
            job);
        message.Content = JsonContent.Create(request);
        HttpResponseMessage response = await _firstWorkerClient.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<SliceJob> AddProcessingJobAsync(Worker worker, byte[]? modelBytes = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicerFileStorage storage = scope.ServiceProvider.GetRequiredService<ISlicerFileStorage>();
        ISliceJobRepository repository = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        string modelUrl = await storage.UploadFileAsync(
            $"worker-models/{Guid.NewGuid():N}.stl",
            modelBytes ?? Encoding.UTF8.GetBytes("solid test-model"),
            "model/stl");
        SliceJob job = new()
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            WorkerId = worker.Id,
            Status = SliceJobStatus.Processing,
            ModelFileUrl = modelUrl,
            ModelFileName = @"D:\private\models\model.stl",
            SlicerEngine = (int)SlicerEngineType.OrcaSlicer,
            SlicerEngineName = SlicerEngineType.OrcaSlicer.ToString(),
            QueuedAt = DateTime.UtcNow.AddMinutes(-1),
            StartedAt = DateTime.UtcNow,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await repository.AddAsync(job);
        await repository.SaveChangesAsync();
        return job;
    }

    /// <summary>
    /// Builds a request that presents the lease a worker was granted for a claimed job.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="requestUri">Target route.</param>
    /// <param name="job">The claimed job whose lease should be presented.</param>
    /// <returns>The prepared request message.</returns>
    private static HttpRequestMessage CreateLeasedRequest(HttpMethod method, string requestUri, SliceJob job)
    {
        HttpRequestMessage request = new(method, requestUri);
        request.Headers.Add(WorkerLeaseHeaders.LeaseToken, job.LeaseToken!.Value.ToString());
        request.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            job.LeaseFence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    private async Task<Worker> GetWorkerAsync(HttpClient client)
    {
        string serviceId = GetServiceId(client).ToString();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return await db.Workers.AsNoTracking().SingleAsync(worker => worker.ServiceId == serviceId);
    }

    private static Guid GetServiceId(HttpClient client) =>
        Guid.Parse(client.DefaultRequestHeaders.GetValues("X-Worker-Id").Single());

    private static MultipartFormDataContent CreateGcodeUpload()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("; generated gcode"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/x.gcode");
        content.Add(file, "file", "result.gcode");
        return content;
    }
}
