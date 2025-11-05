using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Tests HTTP-based completion flow with multiple artifact types (G-code, log, thumbnail).
/// Validates worker authentication integration and artifact aggregation.
/// </summary>
public class SliceJobHttpCompletionWithArtifactsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SliceJobHttpCompletionWithArtifactsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact(DisplayName = "Complete job with G-code, log text, and thumbnail via HTTP")]
    public async Task Complete_Job_With_Multiple_Artifacts_Via_HTTP()
    {
        // Arrange - create a processing job
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "test-model.stl",
            ModelFileUrl = "http://example/test-model.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload G-code artifact (primary) using service
        var gcodeBytes = Encoding.UTF8.GetBytes("; Generated G-code\nG28 ; Home\nG1 X10 Y10 Z0.2 F3000\n; End");
        var gcodeForm = new TestFormFile(gcodeBytes, "output.gcode", "application/gcode");
        var primaryArtifact = await artifactsService.UploadAsync(gcodeForm, job.Id, null, "gcode", default);
        primaryArtifact.Should().NotBeNull();

        // Upload thumbnail artifact (additional) using service
        var thumbnailBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header stub
        var thumbnailForm = new TestFormFile(thumbnailBytes, "thumbnail.png", "image/png");
        var thumbnailArtifact = await artifactsService.UploadAsync(thumbnailForm, job.Id, null, "thumbnail", default);
        thumbnailArtifact.Should().NotBeNull();

        // Complete job with primary, additional artifact, and inline log text
        var completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = primaryArtifact!.Id,
            AdditionalArtifactIds = new[] { thumbnailArtifact!.Id },
            EstimatedPrintTimeSeconds = 3600,
            FilamentUsedGrams = 25.5m,
            LogText = "Slicing started\nProcessing layer 1/100\nProcessing layer 50/100\nSlicing completed successfully"
        };

        var completeRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(completeRequest)
        };
        completeRequestMessage.Headers.Add("X-Worker-Key", "test-worker-key");

        // Act
        var completeResponse = await _client.SendAsync(completeRequestMessage);

        // Assert - Response
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var completeResult = await completeResponse.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();
        completeResult.Should().NotBeNull();
        completeResult!.JobId.Should().Be(job.Id);
        completeResult.Status.Should().Be("Completed");
        completeResult.CompletedAt.Should().NotBeNull();
        completeResult.ResultFileUrl.Should().NotBeNullOrEmpty();
        completeResult.EstimatedPrintTimeSeconds.Should().Be(3600);
        completeResult.FilamentUsedGrams.Should().Be(25.5m);

        // Should have 3 artifacts: primary G-code, thumbnail, and auto-created log
        completeResult.ArtifactIds.Should().HaveCount(3);
        completeResult.ArtifactIds.Should().Contain(primaryArtifact.Id);
        completeResult.ArtifactIds.Should().Contain(thumbnailArtifact.Id);
        completeResult.LogArtifactId.Should().NotBeNull();
        completeResult.ArtifactIds.Should().Contain(completeResult.LogArtifactId!.Value);

        // Assert - Job state persisted (query DB directly to bypass any EF caching issues)
        using var verifyScope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var updatedJob = await verifyDb.SliceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob!.Status.Should().Be(SliceJobStatus.Completed);
        updatedJob.CompletedAt.Should().NotBeNull();
        updatedJob.ResultFileUrl.Should().NotBeNullOrEmpty();
        updatedJob.EstimatedPrintTimeSeconds.Should().Be(3600);
        updatedJob.FilamentUsedGrams.Should().Be(25.5m);
        updatedJob.ArtifactsCount.Should().Be(3);
        updatedJob.ArtifactsTotalBytes.Should().BeGreaterThan(0);

        // Assert - Artifacts retrievable
        var verifyArtifactsService = verifyScope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();
        var artifacts = await verifyArtifactsService.ListByJobAsync(job.Id, default);
        artifacts.Should().HaveCount(3);
        artifacts.Should().Contain(a => a.Id == primaryArtifact.Id && a.Kind == "gcode");
        artifacts.Should().Contain(a => a.Id == thumbnailArtifact.Id && a.Kind == "thumbnail");
        artifacts.Should().Contain(a => a.Kind == "log" && a.FileName == "slicer-log.txt");
    }

    [Fact(DisplayName = "Complete job with only G-code (minimal artifacts)")]
    public async Task Complete_Job_With_Only_Gcode_Minimal()
    {
        // Arrange
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "minimal.stl",
            ModelFileUrl = "http://example/minimal.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload only G-code using service
        var gcodeBytes = Encoding.UTF8.GetBytes("; Minimal G-code\nG28\n");
        var gcodeForm = new TestFormFile(gcodeBytes, "minimal.gcode", "application/gcode");
        var artifact = await artifactsService.UploadAsync(gcodeForm, job.Id, null, "gcode", default);

        // Complete with minimal request (no log, no additional artifacts, no metrics)
        var completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact!.Id
        };

        var completeRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(completeRequest)
        };
        completeRequestMessage.Headers.Add("X-Worker-Key", "test-worker-key");

        // Act
        var completeResponse = await _client.SendAsync(completeRequestMessage);

        // Assert
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var completeResult = await completeResponse.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();
        completeResult.Should().NotBeNull();
        completeResult!.ArtifactIds.Should().HaveCount(1);
        completeResult.ArtifactIds.Should().Contain(artifact.Id);
        completeResult.LogArtifactId.Should().BeNull();
        completeResult.EstimatedPrintTimeSeconds.Should().BeNull();
        completeResult.FilamentUsedGrams.Should().BeNull();

        // Query DB directly to bypass EF caching issues
        using var verifyScope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var updatedJob = await verifyDb.SliceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob!.Status.Should().Be(SliceJobStatus.Completed);
        updatedJob.ArtifactsCount.Should().Be(1);
    }

    [Fact(DisplayName = "Complete job fails with 401 when auth header missing")]
    public async Task Complete_Job_Fails_401_Without_Auth()
    {
        // Arrange
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "auth-test.stl",
            ModelFileUrl = "http://example/auth-test.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload artifact using service
        var gcodeBytes = Encoding.UTF8.GetBytes("; Test G-code\n");
        var gcodeForm = new TestFormFile(gcodeBytes, "test.gcode", "application/gcode");
        var artifact = await artifactsService.UploadAsync(gcodeForm, job.Id, null, "gcode", default);

        // Complete WITHOUT auth header
        var completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact!.Id
        };

        // Act
        var completeResponse = await _client.PostAsJsonAsync($"/api/slice/{job.Id}/complete", completeRequest);

        // Assert
        completeResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Job should still be in Processing state
        var unchangedJob = await jobRepo.GetByIdAsync(job.Id);
        unchangedJob!.Status.Should().Be(SliceJobStatus.Processing);
        unchangedJob.CompletedAt.Should().BeNull();
    }

    [Fact(DisplayName = "Complete job with large log text and multiple thumbnails")]
    public async Task Complete_Job_With_Large_Log_And_Multiple_Thumbnails()
    {
        // Arrange
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-10),
            StartedAt = DateTime.UtcNow.AddMinutes(-8),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "complex-model.3mf",
            ModelFileUrl = "http://example/complex-model.3mf",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload G-code using service
        var gcodeBytes = Encoding.UTF8.GetBytes("; Complex G-code\nG28\nG1 X100 Y100\n");
        var gcodeForm = new TestFormFile(gcodeBytes, "complex.gcode", "application/gcode");
        var gcodeArtifact = await artifactsService.UploadAsync(gcodeForm, job.Id, null, "gcode", default);

        // Upload thumbnail 1 (preview) using service
        var thumb1Bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01 };
        var thumb1Form = new TestFormFile(thumb1Bytes, "preview-small.png", "image/png");
        var thumb1Artifact = await artifactsService.UploadAsync(thumb1Form, job.Id, null, "thumbnail", default);

        // Upload thumbnail 2 (large preview) using service
        var thumb2Bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x02 };
        var thumb2Form = new TestFormFile(thumb2Bytes, "preview-large.png", "image/png");
        var thumb2Artifact = await artifactsService.UploadAsync(thumb2Form, job.Id, null, "thumbnail", default);

        // Generate large log text (simulate verbose slicer output)
        var logBuilder = new StringBuilder();
        for (int i = 1; i <= 1000; i++)
        {
            logBuilder.AppendLine($"[{i:D4}] Processing layer {i}/1000 - progress {i / 10.0:F1}%");
        }
        logBuilder.AppendLine("Slicing completed successfully");
        logBuilder.AppendLine("Total layers: 1000");
        logBuilder.AppendLine("Estimated time: 14h 35m");

        // Complete with all artifacts and large log
        var completeRequest = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = gcodeArtifact!.Id,
            AdditionalArtifactIds = new[] { thumb1Artifact!.Id, thumb2Artifact!.Id },
            EstimatedPrintTimeSeconds = 52500,
            FilamentUsedGrams = 125.75m,
            LogText = logBuilder.ToString()
        };

        var completeRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(completeRequest)
        };
        completeRequestMessage.Headers.Add("X-Worker-Key", "test-worker-key");

        // Act
        var completeResponse = await _client.SendAsync(completeRequestMessage);

        // Assert
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var completeResult = await completeResponse.Content.ReadFromJsonAsync<CompleteSliceJobResponse>();

        // Should have 4 artifacts: gcode + 2 thumbnails + auto-created log
        completeResult!.ArtifactIds.Should().HaveCount(4);
        completeResult.LogArtifactId.Should().NotBeNull();

        // Verify all artifacts persisted (use fresh scope to avoid stale tracking)
        using var verifyScope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var verifyArtifactsService = verifyScope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();
        var artifacts = await verifyArtifactsService.ListByJobAsync(job.Id, default);
        artifacts.Should().HaveCount(4);

        var logArtifact = artifacts.Should().ContainSingle(a => a.Kind == "log").Subject;
        logArtifact.SizeBytes.Should().BeGreaterThan(5000); // Large log should be >5KB

        var updatedJob = await verifyDb.SliceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob!.ArtifactsCount.Should().Be(4);
        updatedJob.ArtifactsTotalBytes.Should().BeGreaterThan(5000);
    }

    private sealed class TestFormFile : IFormFile
    {
        private readonly byte[] _data;
        public TestFormFile(byte[] data, string fileName, string contentType)
        {
            _data = data;
            FileName = fileName;
            ContentType = contentType;
            Name = "file";
            Length = data.Length;
        }
        public string ContentType { get; }
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; }
        public string Name { get; }
        public string FileName { get; }
        public void CopyTo(System.IO.Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(System.IO.Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public System.IO.Stream OpenReadStream() => new System.IO.MemoryStream(_data, writable: false);
    }
}
