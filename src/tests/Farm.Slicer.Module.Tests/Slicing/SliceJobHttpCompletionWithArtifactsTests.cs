using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests HTTP-based completion flow with multiple artifact types (G-code, log, thumbnail).
/// Validates worker authentication integration and artifact aggregation.
/// </summary>
public class SliceJobHttpCompletionWithArtifactsTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public SliceJobHttpCompletionWithArtifactsTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateWorkerClientAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "Complete job with G-code, log text, and thumbnail via HTTP")]
    public async Task Complete_Job_With_Multiple_Artifacts_Via_HTTP()
    {
        // Arrange - create a processing job
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker worker = await db.Workers.SingleAsync();
        Guid claimToken = Guid.NewGuid();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "test-model.stl",
            ModelFileUrl = "http://example/test-model.stl",
            WorkerId = worker.Id,
            ClaimToken = claimToken,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload G-code artifact (primary) using service
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; Generated G-code\nG28 ; Home\nG1 X10 Y10 Z0.2 F3000\n; End");
        TestFormFile gcodeForm = new TestFormFile(gcodeBytes, "output.gcode", "application/gcode");
        Artifact primaryArtifact = (await artifactsService.UploadForActiveLeaseAsync(
            gcodeForm,
            job.Id,
            worker.Id,
            claimToken,
            "gcode",
            default))!;
        _ = primaryArtifact.Should().NotBeNull();

        // Upload thumbnail artifact (additional) using service
        byte[] thumbnailBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header stub
        TestFormFile thumbnailForm = new TestFormFile(thumbnailBytes, "thumbnail.png", "image/png");
        Artifact thumbnailArtifact = (await artifactsService.UploadForActiveLeaseAsync(
            thumbnailForm,
            job.Id,
            worker.Id,
            claimToken,
            "thumbnail",
            default))!;
        _ = thumbnailArtifact.Should().NotBeNull();

        // Complete job with primary, additional artifact, and inline log text
        CompleteSliceJobRequest completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = primaryArtifact!.Id,
            AdditionalArtifactIds = new[] { thumbnailArtifact!.Id },
            EstimatedPrintTimeSeconds = 3600,
            FilamentUsedGrams = 25.5m,
            LogText = "Slicing started\nProcessing layer 1/100\nProcessing layer 50/100\nSlicing completed successfully"
        };

        using HttpRequestMessage completeRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(completeRequest)
        };
        AddLeaseHeaders(completeRequestMessage, job);
        // Act
        HttpResponseMessage completeResponse = await _client.SendAsync(completeRequestMessage);

        // Assert - Response
        _ = completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CompleteSliceJobResponse? completeResult = await completeResponse.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();
        _ = completeResult.Should().NotBeNull();
        _ = completeResult!.JobId.Should().Be(job.Id);
        _ = completeResult.Status.Should().Be("Completed");
        _ = completeResult.CompletedAt.Should().NotBeNull();
        _ = completeResult.ResultFileUrl.Should().NotBeNullOrEmpty();
        _ = completeResult.EstimatedPrintTimeSeconds.Should().Be(3600);
        _ = completeResult.FilamentUsedGrams.Should().Be(25.5m);

        // Untrusted inline worker logs are not persisted or returned.
        _ = completeResult.ArtifactIds.Should().HaveCount(2);
        _ = completeResult.ArtifactIds.Should().Contain(primaryArtifact.Id);
        _ = completeResult.ArtifactIds.Should().Contain(thumbnailArtifact.Id);
        _ = completeResult.LogArtifactId.Should().BeNull();

        // Assert - Job state persisted (query DB directly to bypass any EF caching issues)
        using IServiceScope verifyScope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        SlicerDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob? updatedJob = await verifyDb.Set<SliceJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        _ = updatedJob.Should().NotBeNull();
        _ = updatedJob!.Status.Should().Be(SliceJobStatus.Completed);
        _ = updatedJob.CompletedAt.Should().NotBeNull();
        _ = updatedJob.ResultFileUrl.Should().NotBeNullOrEmpty();
        _ = updatedJob.EstimatedPrintTimeSeconds.Should().Be(3600);
        _ = updatedJob.FilamentUsedGrams.Should().Be(25.5m);
        _ = updatedJob.ArtifactsCount.Should().Be(2);
        _ = updatedJob.ArtifactsTotalBytes.Should().BeGreaterThan(0);

        // Assert - Artifacts retrievable
        IArtifactsService verifyArtifactsService = verifyScope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IReadOnlyList<Artifact> artifacts = await verifyArtifactsService.ListByJobAsync(job.Id, default);
        _ = artifacts.Should().HaveCount(2);
        _ = artifacts.Should().Contain(a => a.Id == primaryArtifact.Id && a.Kind == "gcode");
        _ = artifacts.Should().Contain(a => a.Id == thumbnailArtifact.Id && a.Kind == "thumbnail");
        _ = artifacts.Should().NotContain(a => a.Kind == "log");
    }

    [Fact(DisplayName = "Complete job with only G-code (minimal artifacts)")]
    public async Task Complete_Job_With_Only_Gcode_Minimal()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker worker = await db.Workers.SingleAsync();
        Guid claimToken = Guid.NewGuid();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "minimal.stl",
            ModelFileUrl = "http://example/minimal.stl",
            WorkerId = worker.Id,
            ClaimToken = claimToken,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload only G-code using service
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; Minimal G-code\nG28\n");
        TestFormFile gcodeForm = new TestFormFile(gcodeBytes, "minimal.gcode", "application/gcode");
        Artifact artifact = (await artifactsService.UploadForActiveLeaseAsync(
            gcodeForm,
            job.Id,
            worker.Id,
            claimToken,
            "gcode",
            default))!;

        // Complete with minimal request (no log, no additional artifacts, no metrics)
        CompleteSliceJobRequest completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact!.Id
        };

        using HttpRequestMessage completeRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(completeRequest)
        };
        AddLeaseHeaders(completeRequestMessage, job);
        // Act
        HttpResponseMessage completeResponse = await _client.SendAsync(completeRequestMessage);

        // Assert
        _ = completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CompleteSliceJobResponse? completeResult = await completeResponse.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();
        _ = completeResult.Should().NotBeNull();
        _ = completeResult!.ArtifactIds.Should().HaveCount(1);
        _ = completeResult.ArtifactIds.Should().Contain(artifact.Id);
        _ = completeResult.LogArtifactId.Should().BeNull();
        _ = completeResult.EstimatedPrintTimeSeconds.Should().BeNull();
        _ = completeResult.FilamentUsedGrams.Should().BeNull();

        // Query DB directly to bypass EF caching issues
        using IServiceScope verifyScope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        SlicerDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob? updatedJob = await verifyDb.Set<SliceJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        _ = updatedJob!.Status.Should().Be(SliceJobStatus.Completed);
        _ = updatedJob.ArtifactsCount.Should().Be(1);
    }

    [Fact(DisplayName = "Complete job fails with 401 when auth header missing")]
    public async Task Complete_Job_Fails_401_Without_Auth()
    {
        // Arrange - use a client without worker key header
        using HttpClient clientWithoutWorkerKey = await _factory.CreateAdminClientAsync();

        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker worker = await db.Workers.SingleAsync();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "auth-test.stl",
            ModelFileUrl = "http://example/auth-test.stl",
            WorkerId = worker.Id,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload artifact using service
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; Test G-code\n");
        TestFormFile gcodeForm = new TestFormFile(gcodeBytes, "test.gcode", "application/gcode");
        Artifact artifact = await artifactsService.UploadAsync(gcodeForm, job.Id, null, "gcode", default);

        // Complete WITHOUT worker key header
        CompleteSliceJobRequest completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact!.Id
        };

        // Act
        HttpResponseMessage completeResponse = await clientWithoutWorkerKey.PostAsJsonAsync($"/api/slice/{job.Id}/complete", completeRequest);

        // Assert
        _ = completeResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Job should still be in Processing state
        SliceJob? unchangedJob = await jobRepo.GetByIdAsync(job.Id);
        _ = unchangedJob!.Status.Should().Be(SliceJobStatus.Processing);
        _ = unchangedJob.CompletedAt.Should().BeNull();
    }

    [Fact(DisplayName = "Complete job with large log text and multiple thumbnails")]
    public async Task Complete_Job_With_Large_Log_And_Multiple_Thumbnails()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker worker = await db.Workers.SingleAsync();
        Guid claimToken = Guid.NewGuid();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-10),
            StartedAt = DateTime.UtcNow.AddMinutes(-8),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "complex-model.3mf",
            ModelFileUrl = "http://example/complex-model.3mf",
            WorkerId = worker.Id,
            ClaimToken = claimToken,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload G-code using service
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; Complex G-code\nG28\nG1 X100 Y100\n");
        TestFormFile gcodeForm = new TestFormFile(gcodeBytes, "complex.gcode", "application/gcode");
        Artifact gcodeArtifact = (await artifactsService.UploadForActiveLeaseAsync(
            gcodeForm,
            job.Id,
            worker.Id,
            claimToken,
            "gcode",
            default))!;

        // Upload thumbnail 1 (preview) using service
        byte[] thumb1Bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01 };
        TestFormFile thumb1Form = new TestFormFile(thumb1Bytes, "preview-small.png", "image/png");
        Artifact thumb1Artifact = (await artifactsService.UploadForActiveLeaseAsync(
            thumb1Form,
            job.Id,
            worker.Id,
            claimToken,
            "thumbnail",
            default))!;

        // Upload thumbnail 2 (large preview) using service
        byte[] thumb2Bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x02 };
        TestFormFile thumb2Form = new TestFormFile(thumb2Bytes, "preview-large.png", "image/png");
        Artifact thumb2Artifact = (await artifactsService.UploadForActiveLeaseAsync(
            thumb2Form,
            job.Id,
            worker.Id,
            claimToken,
            "thumbnail",
            default))!;

        // Generate large log text (simulate verbose slicer output)
        StringBuilder logBuilder = new StringBuilder();
        for (int i = 1; i <= 1000; i++)
        {
            _ = logBuilder.AppendLine($"[{i:D4}] Processing layer {i}/1000 - progress {i / 10.0:F1}%");
        }
        _ = logBuilder.AppendLine("Slicing completed successfully");
        _ = logBuilder.AppendLine("Total layers: 1000");
        _ = logBuilder.AppendLine("Estimated time: 14h 35m");

        // Complete with all artifacts and large log
        CompleteSliceJobRequest completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = gcodeArtifact!.Id,
            AdditionalArtifactIds = new[] { thumb1Artifact!.Id, thumb2Artifact!.Id },
            EstimatedPrintTimeSeconds = 52500,
            FilamentUsedGrams = 125.75m,
            LogText = logBuilder.ToString()
        };

        using HttpRequestMessage completeRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(completeRequest)
        };
        AddLeaseHeaders(completeRequestMessage, job);
        // Act
        HttpResponseMessage completeResponse = await _client.SendAsync(completeRequestMessage);

        // Assert
        _ = completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        CompleteSliceJobResponse? completeResult = await completeResponse.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();

        // Inline logs are untrusted worker data and are intentionally ignored.
        _ = completeResult!.ArtifactIds.Should().HaveCount(3);
        _ = completeResult.LogArtifactId.Should().BeNull();

        // Verify all artifacts persisted (use fresh scope to avoid stale tracking)
        using IServiceScope verifyScope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        SlicerDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        IArtifactsService verifyArtifactsService = verifyScope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IReadOnlyList<Artifact> artifacts = await verifyArtifactsService.ListByJobAsync(job.Id, default);
        _ = artifacts.Should().HaveCount(3);
        _ = artifacts.Should().NotContain(a => a.Kind == "log");

        SliceJob? updatedJob = await verifyDb.Set<SliceJob>().AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        _ = updatedJob!.ArtifactsCount.Should().Be(3);
        _ = updatedJob.ArtifactsTotalBytes.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Presents the lease the job was claimed under so the fenced worker contract accepts the call.
    /// </summary>
    /// <param name="request">The outgoing worker request.</param>
    /// <param name="job">The claimed job.</param>
    private static void AddLeaseHeaders(HttpRequestMessage request, SliceJob job)
    {
        request.Headers.Add(WorkerClaimHeaders.ClaimToken, job.ClaimToken!.Value.ToString());
        request.Headers.Add(WorkerLeaseHeaders.LeaseToken, job.LeaseToken!.Value.ToString());
        request.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            job.LeaseFence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class TestFormFile(byte[] data, string fileName, string contentType) : IFormFile
    {
        private readonly byte[] _data = data;

        public string ContentType { get; } = contentType;
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; } = data.Length;
        public string Name { get; } = "file";
        public string FileName { get; } = fileName;
        public void CopyTo(Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
    }
}
