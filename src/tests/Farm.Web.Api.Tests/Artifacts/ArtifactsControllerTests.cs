using Farm.Web.Api.Tests.TestInfrastructure;
﻿using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Artifacts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Artifacts;

[Collection(IntegrationTestCollection.Name)]
public class ArtifactsControllerTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Artifact service upload + file presence works")]
    public async Task Artifact_Upload_And_File_Persisted()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid jobId = Guid.NewGuid();
        // Minimal job row required to satisfy FK-less artifact (jobId stored but SliceJob not strictly required yet)
        SliceJob sliceJob = new SliceJob
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
        _ = db.SliceJobs.Add(sliceJob);
        _ = await db.SaveChangesAsync();

        string kind = "gcode";
        byte[] data = Encoding.UTF8.GetBytes("G1 X0 Y0 Z0 F1500 ; test gcode");
        TestFormFile formFile = new TestFormFile(data, "test.gcode", "text/plain");
        Artifact artifact = await artifactsService.UploadAsync(formFile, jobId, null, kind, default);

        _ = artifact.Kind.Should().Be(kind);
        _ = artifact.SizeBytes.Should().Be(data.Length);
        _ = artifact.FileName.Should().Be("test.gcode");

        (Artifact artifact, string fullPath)? pathInfo = await artifactsService.GetWithPathAsync(artifact.Id, default);
        _ = pathInfo.Should().NotBeNull();
        string fullPath = pathInfo!.Value.fullPath;
        _ = File.Exists(fullPath).Should().BeTrue();
        byte[] fileBytes = await File.ReadAllBytesAsync(fullPath);
        _ = fileBytes.Length.Should().Be(data.Length);
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
