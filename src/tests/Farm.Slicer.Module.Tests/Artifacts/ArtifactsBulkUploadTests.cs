using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.DTOs.Artifacts;
using Farm.Web.Api.Services.Artifacts;
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

    [Fact(DisplayName = "Bulk upload multiple artifacts succeeds")]
    public async Task Bulk_Upload_Multiple_Artifacts_Succeeds()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<ArtifactStorageSettings> settings = Options.Create(new ArtifactStorageSettings { AllowedKinds = "gcode,thumbnail,log" });
        // Provide stub slice job repository + settings to satisfy new controller signature
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(service, jobRepo, settings);

        Guid jobId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();

        FormFileCollection files = new FormFileCollection
        {
            CreateFormFile(Encoding.UTF8.GetBytes("G1 X10 Y10"), "test.gcode", "application/x-gcode"),
            CreateFormFile(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "preview.png", "image/png"),
            CreateFormFile(Encoding.UTF8.GetBytes("log data"), "output.log", "text/plain")
        };

        // Act
        IActionResult result = await controller.BulkUploadAsync(jobId, workerId, files, CancellationToken.None);

        // Assert
        _ = result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        IEnumerable<ArtifactDto>? artifacts = okResult.Value as IEnumerable<ArtifactDto>;
        _ = artifacts.Should().NotBeNull();
        _ = artifacts.Should().HaveCount(3);
        _ = artifacts!.Select(a => a.Kind).Should().Contain(new[] { "gcode", "thumbnail", "log" });
    }

    [Fact(DisplayName = "Bulk upload with no files returns 400")]
    public async Task Bulk_Upload_With_No_Files_Returns_BadRequest()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<ArtifactStorageSettings> settings = Options.Create(new ArtifactStorageSettings());
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(service, jobRepo, settings);

        FormFileCollection files = new FormFileCollection();

        // Act
        IActionResult result = await controller.BulkUploadAsync(Guid.NewGuid(), null, files, CancellationToken.None);

        // Assert
        _ = result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(DisplayName = "Bulk upload infers kind from file extension")]
    public async Task Bulk_Upload_Infers_Kind_From_Extension()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        IOptions<ArtifactStorageSettings> settings = Options.Create(new ArtifactStorageSettings { AllowedKinds = "gcode,thumbnail" });
        JobDispatcherServiceTests.StubSliceJobRepository jobRepo = new JobDispatcherServiceTests.StubSliceJobRepository();
        ArtifactsController controller = new ArtifactsController(service, jobRepo, settings);

        Guid jobId = Guid.NewGuid();
        FormFileCollection files = new FormFileCollection
        {
            CreateFormFile(Encoding.UTF8.GetBytes("gcode"), "model.gcode", "application/octet-stream"),
            CreateFormFile(new byte[] { 1, 2, 3 }, "thumb.png", "application/octet-stream")
        };

        // Act
        IActionResult result = await controller.BulkUploadAsync(jobId, null, files, CancellationToken.None);

        // Assert
        _ = result.Should().BeOfType<OkObjectResult>();
        OkObjectResult okResult = (OkObjectResult)result;
        IEnumerable<ArtifactDto>? artifacts = okResult.Value as IEnumerable<ArtifactDto>;
        _ = artifacts.Should().HaveCount(2);
        _ = artifacts!.Select(a => a.Kind).Should().Contain(new[] { "gcode", "thumbnail" });
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
