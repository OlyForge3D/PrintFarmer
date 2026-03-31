using System;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Tests.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts;

[Collection(IntegrationTestCollection.Name)]
public class ArtifactsControllerKindValidationTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Upload with empty file returns 400 (controller direct)")]
    public async Task Upload_Empty_File_Returns_BadRequest()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<SlicerArtifactStorageSettings> opts = Options.Create(new SlicerArtifactStorageSettings());
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
        TestFormFile file = new TestFormFile(Array.Empty<byte>(), "a.txt", "text/plain");
        IActionResult result = await controller.UploadAsync(Guid.NewGuid(), file, default);
        _ = result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(DisplayName = "Upload with file exceeding max size returns 400")]
    public async Task Upload_Oversized_File_Returns_BadRequest()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService svc = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<SlicerArtifactStorageSettings> opts = Options.Create(new SlicerArtifactStorageSettings { MaxFileSizeBytes = 10 });
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(svc, jobRepo, opts);
        TestFormFile file = new TestFormFile(new byte[20], "large.gcode", "application/octet-stream");
        IActionResult result = await controller.UploadAsync(Guid.NewGuid(), file, default);
        _ = result.Should().BeOfType<BadRequestObjectResult>();
    }

    private sealed class TestFormFile(byte[] d, string name, string ct) : IFormFile
    {
        private readonly byte[] _data = d;

        public string ContentType { get; } = ct;
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; } = d.Length;
        public string Name { get; } = "file";
        public string FileName { get; } = name;
        public void CopyTo(Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
    }
}
