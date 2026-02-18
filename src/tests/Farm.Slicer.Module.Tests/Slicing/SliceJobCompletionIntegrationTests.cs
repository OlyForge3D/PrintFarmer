using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Artifacts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// End-to-end integration test for slice job completion flow:
/// Submit -> Claim -> Upload artifact -> Complete -> Verify status & URL.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobCompletionIntegrationTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Slice job completion repository/service flow succeeds (single artifact)")]
    public async Task SliceJob_Service_Completion_Flow_Succeeds()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        // 1. Create processing job
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            StartedAt = DateTime.UtcNow.AddMinutes(-4),
            SlicerEngine = 0,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // 2. Upload artifact via service
        byte[] bytes = Encoding.UTF8.GetBytes("; G-code test contents");
        TestFormFile formFile = new TestFormFile(bytes, "output.gcode", "application/gcode");
        Artifact artifact = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);
        _ = artifact.Id.Should().NotBe(Guid.Empty);
        _ = artifact.JobId.Should().Be(job.Id);
        _ = artifact.Kind.Should().Be("gcode");

        // 3. Complete job
        string resultUrl = $"/api/artifacts/{artifact.Id}/download";
        await jobRepo.MarkCompletedWithArtifactsAsync(job.Id, resultUrl, new[] { artifact.Id }, 1234, 15.6m);
        await jobRepo.SaveChangesAsync();

        // 4. Reload and assert
        SliceJob? updated = await jobRepo.GetByIdAsync(job.Id);
        _ = updated.Should().NotBeNull();
        _ = updated!.Status.Should().Be(SliceJobStatus.Completed);
        _ = updated.ResultFileUrl.Should().Be(resultUrl);
        _ = updated.ProgressPercent.Should().Be(100);
        _ = updated.EstimatedPrintTimeSeconds.Should().Be(1234);
        _ = updated.FilamentUsedGrams.Should().Be(15.6m);
        _ = updated.ArtifactsCount.Should().Be(1);
        _ = updated.ArtifactsTotalBytes.Should().BeGreaterThan(0);
        _ = updated.ArtifactIdsCsv.Should().Contain(artifact.Id.ToString());

        // Parse CSV correctness
        string[] ids = updated.ArtifactIdsCsv!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _ = ids.Should().HaveCount(1);
        _ = Guid.Parse(ids[0]).Should().Be(artifact.Id);
    }

    [Fact(DisplayName = "Slice job completion persists multi-artifact summary")]
    public async Task SliceJob_Completion_MultiArtifact_Summary()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            SlicerEngine = 0,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload multiple artifacts
        Artifact gcode = await artifactsService.UploadAsync(new TestFormFile(Encoding.UTF8.GetBytes(";gcode"), "a.gcode", "application/gcode"), job.Id, null, "gcode", default);
        Artifact preview = await artifactsService.UploadAsync(new TestFormFile(new byte[] { 1, 2, 3, 4, 5 }, "preview.png", "image/png"), job.Id, null, "preview", default);
        Artifact log = await artifactsService.UploadTextAsync("Log line A\nLog line B", "log.txt", job.Id, null, "log", default);

        Guid[] artifactIds = new[] { gcode.Id, preview.Id, log.Id };
        string resultUrl = $"/api/artifacts/{gcode.Id}/download";
        await jobRepo.MarkCompletedWithArtifactsAsync(job.Id, resultUrl, artifactIds, 999, 3.2m);
        await jobRepo.SaveChangesAsync();

        SliceJob? updated = await jobRepo.GetByIdAsync(job.Id);
        _ = updated.Should().NotBeNull();
        _ = updated!.ArtifactsCount.Should().Be(3);
        _ = updated.ArtifactIdsCsv.Should().NotBeNull();
        _ = updated.ArtifactIdsCsv!.Split(',').Length.Should().Be(3);
        _ = updated.ArtifactsTotalBytes.Should().BeGreaterThanOrEqualTo(gcode.SizeBytes + preview.SizeBytes + log.SizeBytes);
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
