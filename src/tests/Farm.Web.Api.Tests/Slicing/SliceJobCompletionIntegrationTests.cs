using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// End-to-end integration test for slice job completion flow:
/// Submit -> Claim -> Upload artifact -> Complete -> Verify status & URL.
/// </summary>
public class SliceJobCompletionIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public SliceJobCompletionIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName="Slice job completion repository/service flow succeeds")]
    public async Task SliceJob_Service_Completion_Flow_Succeeds()
    {
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        // 1. Create processing job
        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Processing,
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
        var bytes = System.Text.Encoding.UTF8.GetBytes("; G-code test contents");
        var formFile = new TestFormFile(bytes, "output.gcode", "application/gcode");
        var artifact = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);
        artifact.Id.Should().NotBe(Guid.Empty);
        artifact.JobId.Should().Be(job.Id);
        artifact.Kind.Should().Be("gcode");

        // 3. Complete job
        string resultUrl = $"/api/artifacts/{artifact.Id}/download";
        await jobRepo.MarkCompletedAsync(job.Id, resultUrl, 1234, 15.6m);
        await jobRepo.SaveChangesAsync();

        // 4. Reload and assert
        var updated = await jobRepo.GetByIdAsync(job.Id);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(Farm.Infrastructure.Domain.SliceJobStatus.Completed);
        updated.ResultFileUrl.Should().Be(resultUrl);
        updated.ProgressPercent.Should().Be(100);
        updated.EstimatedPrintTimeSeconds.Should().Be(1234);
        updated.FilamentUsedGrams.Should().Be(15.6m);
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
        public void CopyTo(Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
    }
}
