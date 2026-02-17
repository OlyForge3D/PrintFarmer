using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Tests.Slicing;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts;

[Collection(IntegrationTestCollection.Name)]
public class ArtifactsBulkUploadTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Upload artifact succeeds for existing job")]
    public async Task Upload_Artifact_Succeeds_For_Existing_Job()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<ArtifactStorageSettings> settings = Options.Create(new ArtifactStorageSettings());
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();

        Guid jobId = Guid.NewGuid();
        jobRepo.Jobs.Add(new SliceJob { Id = jobId, Status = SliceJobStatus.Processing });

        ArtifactsController controller = new ArtifactsController(service, jobRepo, settings);

        IFormFile file = CreateFormFile(Encoding.UTF8.GetBytes("G1 X10 Y10"), "test.gcode", "application/x-gcode");

        // Act
        IActionResult result = await controller.UploadAsync(jobId, file, CancellationToken.None);

        // Assert
        _ = result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact(DisplayName = "Upload with empty file returns 400")]
    public async Task Upload_With_Empty_File_Returns_BadRequest()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<ArtifactStorageSettings> settings = Options.Create(new ArtifactStorageSettings());
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(service, jobRepo, settings);

        IFormFile emptyFile = CreateFormFile(Array.Empty<byte>(), "empty.gcode", "application/x-gcode");

        // Act
        IActionResult result = await controller.UploadAsync(Guid.NewGuid(), emptyFile, CancellationToken.None);

        // Assert
        _ = result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(DisplayName = "Upload with non-existent job returns 404")]
    public async Task Upload_With_NonExistent_Job_Returns_NotFound()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<ArtifactStorageSettings> settings = Options.Create(new ArtifactStorageSettings());
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(service, jobRepo, settings);

        IFormFile file = CreateFormFile(Encoding.UTF8.GetBytes("gcode"), "model.gcode", "application/octet-stream");

        // Act
        IActionResult result = await controller.UploadAsync(Guid.NewGuid(), file, CancellationToken.None);

        // Assert
        _ = result.Should().BeOfType<NotFoundObjectResult>();
    }

    private static IFormFile CreateFormFile(byte[] content, string fileName, string contentType)
    {
        MemoryStream stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }
}
