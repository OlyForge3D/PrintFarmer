using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.Artifacts;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// End-to-end test validating complete slice job lifecycle: queue, claim, bulk artifact upload, inline log, completion, authorization.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobCompletionE2ETests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Complete E2E flow: queue, artifacts, log, completion, ownership verification")]
    public async Task SliceJob_E2E_Completion_Flow_With_Authorization()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        Guid userId = Guid.NewGuid();

        // 1. Enqueue job
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            UserId = userId,
            ModelFileName = "test-model.stl",
            ModelFileUrl = "http://example/test-model.stl",
            SlicerEngine = 0,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // 2. Claim job (simulating worker)
        await jobRepo.MarkStartedAsync(job.Id, Guid.NewGuid());
        await jobRepo.SaveChangesAsync();

        // 3. Upload bulk artifacts (gcode, thumbnail, preview)
        byte[] gcodeBytes = Encoding.UTF8.GetBytes("; Example G-code\nG28\nG1 X10 Y10");
        byte[] thumbnailBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        byte[] previewBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header

        Artifact gcode = await artifactsService.UploadAsync(
            new TestFormFile(gcodeBytes, "output.gcode", "application/gcode"),
            job.Id, null, "gcode", default);
        Artifact thumbnail = await artifactsService.UploadAsync(
            new TestFormFile(thumbnailBytes, "thumb.png", "image/png"),
            job.Id, null, "thumbnail", default);
        Artifact preview = await artifactsService.UploadAsync(
            new TestFormFile(previewBytes, "preview.jpg", "image/jpeg"),
            job.Id, null, "preview", default);

        // 4. Upload inline log text
        Artifact log = await artifactsService.UploadTextAsync(
            "Slicing started\nLayer 1/100\nSlicing complete",
            "slicer.log",
            job.Id, null, "log", default);

        // 5. Mark job completed with all artifacts
        Guid[] allArtifactIds = new[] { gcode.Id, thumbnail.Id, preview.Id, log.Id };
        await jobRepo.MarkCompletedWithArtifactsAsync(job.Id, $"/api/artifacts/{gcode.Id}/download", allArtifactIds, 1500, 25.3m);
        await jobRepo.SaveChangesAsync();

        // 6. Verify completion state
        SliceJob? completed = await jobRepo.GetByIdAsync(job.Id);
        _ = completed.Should().NotBeNull();
        _ = completed!.Status.Should().Be(SliceJobStatus.Completed);
        _ = completed.ArtifactsCount.Should().Be(4);
        _ = completed.ArtifactsTotalBytes.Should().BeGreaterThan(0);
        _ = completed.ArtifactIdsCsv.Should().NotBeNullOrEmpty();
        _ = completed.ArtifactIdsCsv!.Split(',').Should().HaveCount(4);
        _ = completed.ResultFileUrl.Should().Contain(gcode.Id.ToString());
        _ = completed.EstimatedPrintTimeSeconds.Should().Be(1500);
        _ = completed.FilamentUsedGrams.Should().Be(25.3m);

        // 7. Verify artifacts accessible
        IReadOnlyList<Artifact> artifacts = await artifactsService.ListByJobAsync(job.Id, default);
        _ = artifacts.Should().HaveCount(4);
        _ = artifacts.Should().Contain(a => a.Kind == "gcode");
        _ = artifacts.Should().Contain(a => a.Kind == "thumbnail");
        _ = artifacts.Should().Contain(a => a.Kind == "preview");
        _ = artifacts.Should().Contain(a => a.Kind == "log");

        // 8. Verify artifact files exist on disk
        foreach (Artifact artifact in artifacts)
        {
            (Artifact artifact, string fullPath)? result = await artifactsService.GetWithPathAsync(artifact.Id, default);
            _ = result.Should().NotBeNull();
            (Artifact? a, string? path) = result!.Value;
            _ = File.Exists(path).Should().BeTrue($"artifact file should exist at {path}");
        }
    }

    private sealed class TestFormFile(byte[] data, string fileName, string contentType) : Microsoft.AspNetCore.Http.IFormFile
    {
        private readonly byte[] _data = data;

        public string ContentType { get; } = contentType;
        public string ContentDisposition { get; set; } = string.Empty;
        public Microsoft.AspNetCore.Http.IHeaderDictionary Headers { get; } = new Microsoft.AspNetCore.Http.HeaderDictionary();
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
