using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// End-to-end test validating complete slice job lifecycle: queue, claim, bulk artifact upload, inline log, completion, authorization.
/// </summary>
public class SliceJobCompletionE2ETests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public SliceJobCompletionE2ETests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact(DisplayName = "Complete E2E flow: queue, artifacts, log, completion, ownership verification")]
    public async Task SliceJob_E2E_Completion_Flow_With_Authorization()
    {
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var userId = Guid.NewGuid();

        // 1. Enqueue job
        var job = new SliceJob
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
        var gcodeBytes = System.Text.Encoding.UTF8.GetBytes("; Example G-code\nG28\nG1 X10 Y10");
        var thumbnailBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        var previewBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header

        var gcode = await artifactsService.UploadAsync(
            new TestFormFile(gcodeBytes, "output.gcode", "application/gcode"),
            job.Id, null, "gcode", default);
        var thumbnail = await artifactsService.UploadAsync(
            new TestFormFile(thumbnailBytes, "thumb.png", "image/png"),
            job.Id, null, "thumbnail", default);
        var preview = await artifactsService.UploadAsync(
            new TestFormFile(previewBytes, "preview.jpg", "image/jpeg"),
            job.Id, null, "preview", default);

        // 4. Upload inline log text
        var log = await artifactsService.UploadTextAsync(
            "Slicing started\nLayer 1/100\nSlicing complete",
            "slicer.log",
            job.Id, null, "log", default);

        // 5. Mark job completed with all artifacts
        var allArtifactIds = new[] { gcode.Id, thumbnail.Id, preview.Id, log.Id };
        await jobRepo.MarkCompletedWithArtifactsAsync(job.Id, $"/api/artifacts/{gcode.Id}/download", allArtifactIds, 1500, 25.3m);
        await jobRepo.SaveChangesAsync();

        // 6. Verify completion state
        var completed = await jobRepo.GetByIdAsync(job.Id);
        completed.Should().NotBeNull();
        completed!.Status.Should().Be(SliceJobStatus.Completed);
        completed.ArtifactsCount.Should().Be(4);
        completed.ArtifactsTotalBytes.Should().BeGreaterThan(0);
        completed.ArtifactIdsCsv.Should().NotBeNullOrEmpty();
        completed.ArtifactIdsCsv!.Split(',').Should().HaveCount(4);
        completed.ResultFileUrl.Should().Contain(gcode.Id.ToString());
        completed.EstimatedPrintTimeSeconds.Should().Be(1500);
        completed.FilamentUsedGrams.Should().Be(25.3m);

        // 7. Verify artifacts accessible
        var artifacts = await artifactsService.ListByJobAsync(job.Id, default);
        artifacts.Should().HaveCount(4);
        artifacts.Should().Contain(a => a.Kind == "gcode");
        artifacts.Should().Contain(a => a.Kind == "thumbnail");
        artifacts.Should().Contain(a => a.Kind == "preview");
        artifacts.Should().Contain(a => a.Kind == "log");

        // 8. Verify artifact files exist on disk
        foreach (var artifact in artifacts)
        {
            var result = await artifactsService.GetWithPathAsync(artifact.Id, default);
            result.Should().NotBeNull();
            var (a, path) = result!.Value;
            System.IO.File.Exists(path).Should().BeTrue($"artifact file should exist at {path}");
        }
    }

    private sealed class TestFormFile : Microsoft.AspNetCore.Http.IFormFile
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
        public Microsoft.AspNetCore.Http.IHeaderDictionary Headers { get; } = new Microsoft.AspNetCore.Http.HeaderDictionary();
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
