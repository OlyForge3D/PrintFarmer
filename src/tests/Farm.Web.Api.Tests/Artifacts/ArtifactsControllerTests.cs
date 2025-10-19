using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Artifacts;

public class ArtifactsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ArtifactsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName="Artifact service upload + file presence works")]
    public async Task Artifact_Upload_And_File_Persisted()
    {
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();

        Guid jobId = Guid.NewGuid();
        // Minimal job row required to satisfy FK-less artifact (jobId stored but SliceJob not strictly required yet)
        var sliceJob = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SlicerEngine = 0,
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        db.SliceJobs.Add(sliceJob);
        await db.SaveChangesAsync();

        string kind = "gcode";
        byte[] data = System.Text.Encoding.UTF8.GetBytes("G1 X0 Y0 Z0 F1500 ; test gcode");
        var formFile = new TestFormFile(data, "test.gcode", "text/plain");
        var artifact = await artifactsService.UploadAsync(formFile, jobId, null, kind, default);

        artifact.Kind.Should().Be(kind);
        artifact.SizeBytes.Should().Be(data.Length);
        artifact.FileName.Should().Be("test.gcode");

        var pathInfo = await artifactsService.GetWithPathAsync(artifact.Id, default);
        pathInfo.Should().NotBeNull();
        var fullPath = pathInfo!.Value.fullPath;
        File.Exists(fullPath).Should().BeTrue();
        var fileBytes = await File.ReadAllBytesAsync(fullPath);
        fileBytes.Length.Should().Be(data.Length);
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
