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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Artifacts;

public class ArtifactsBulkUploadTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ArtifactsBulkUploadTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "Bulk upload multiple artifacts succeeds")]
    public async Task Bulk_Upload_Multiple_Artifacts_Succeeds()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        var settings = Options.Create(new ArtifactStorageSettings { AllowedKinds = "gcode,thumbnail,log" });
        // Provide stub slice job repository + settings to satisfy new controller signature
        var jobRepo = new Farm.Web.Api.Tests.Slicing.JobDispatcherServiceTests.StubSliceJobRepository();
        var controller = new ArtifactsController(service, jobRepo, settings);

        var jobId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        var files = new FormFileCollection
        {
            CreateFormFile(Encoding.UTF8.GetBytes("G1 X10 Y10"), "test.gcode", "application/x-gcode"),
            CreateFormFile(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "preview.png", "image/png"),
            CreateFormFile(Encoding.UTF8.GetBytes("log data"), "output.log", "text/plain")
        };

        // Act
        var result = await controller.BulkUploadAsync(jobId, workerId, files, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var artifacts = okResult.Value as IEnumerable<ArtifactDto>;
        artifacts.Should().NotBeNull();
        artifacts.Should().HaveCount(3);
        artifacts!.Select(a => a.Kind).Should().Contain(new[] { "gcode", "thumbnail", "log" });
    }

    [Fact(DisplayName = "Bulk upload with no files returns 400")]
    public async Task Bulk_Upload_With_No_Files_Returns_BadRequest()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        var settings = Options.Create(new ArtifactStorageSettings());
        var jobRepo = new Farm.Web.Api.Tests.Slicing.JobDispatcherServiceTests.StubSliceJobRepository();
        var controller = new ArtifactsController(service, jobRepo, settings);

        var files = new FormFileCollection();

        // Act
        var result = await controller.BulkUploadAsync(Guid.NewGuid(), null, files, CancellationToken.None);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact(DisplayName = "Bulk upload infers kind from file extension")]
    public async Task Bulk_Upload_Infers_Kind_From_Extension()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        var settings = Options.Create(new ArtifactStorageSettings { AllowedKinds = "gcode,thumbnail" });
        var jobRepo = new Farm.Web.Api.Tests.Slicing.JobDispatcherServiceTests.StubSliceJobRepository();
        var controller = new ArtifactsController(service, jobRepo, settings);

        var jobId = Guid.NewGuid();
        var files = new FormFileCollection
        {
            CreateFormFile(Encoding.UTF8.GetBytes("gcode"), "model.gcode", "application/octet-stream"),
            CreateFormFile(new byte[] { 1, 2, 3 }, "thumb.png", "application/octet-stream")
        };

        // Act
        var result = await controller.BulkUploadAsync(jobId, null, files, CancellationToken.None);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = (OkObjectResult)result;
        var artifacts = okResult.Value as IEnumerable<ArtifactDto>;
        artifacts.Should().HaveCount(2);
        artifacts!.Select(a => a.Kind).Should().Contain(new[] { "gcode", "thumbnail" });
    }

    private static IFormFile CreateFormFile(byte[] content, string fileName, string contentType)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }
}
