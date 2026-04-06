using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Regression tests for model picker display name contract.
/// These tests ensure the model picker shows original uploaded file names,
/// not internal GUID-based storage names.
/// </summary>
public class Model3DDisplayNameRegressionTests
{
    private static Model3DFilesController CreateController(
        Mock<IModel3DFileService> mockService,
        Mock<I3MfToStlConversionService>? mockConverter = null)
    {
        var mockLogger = new Mock<ILogger<Model3DFilesController>>();
        mockConverter ??= new Mock<I3MfToStlConversionService>();

        return new Model3DFilesController(
            mockLogger.Object,
            mockService.Object,
            mockConverter.Object);
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsOriginalFileNameInNameProperty()
    {
        // Arrange: Service returns models with original file names in Name property
        var mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        var expectedModels = new List<Model3DDto>
        {
            new Model3DDto
            {
                Id = Guid.NewGuid(),
                FileName = "a3b5c7d9-model.stl", // GUID-based storage name
                Name = "my-cool-benchy.stl", // Original uploaded name
                FileType = "stl",
                FileSize = 1024
            },
            new Model3DDto
            {
                Id = Guid.NewGuid(),
                FileName = "f1e2d3c4-model.stl",
                Name = "calibration-cube.stl",
                FileType = "stl",
                FileSize = 512
            }
        };
        mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModels);

        var controller = CreateController(mockService);

        // Act: Call the endpoint
        var result = await controller.ListModelsAsync();

        // Assert: Verify response contains Name property with original file names
        var okResult = Assert.IsType<OkObjectResult>(result);
        var models = Assert.IsAssignableFrom<IEnumerable<Model3DDto>>(okResult.Value);
        var modelsList = models.ToList();

        Assert.Equal(2, modelsList.Count);
        Assert.Equal("my-cool-benchy.stl", modelsList[0].Name);
        Assert.Equal("calibration-cube.stl", modelsList[1].Name);

        // Verify storage names are different from display names
        Assert.NotEqual(modelsList[0].FileName, modelsList[0].Name);
        Assert.NotEqual(modelsList[1].FileName, modelsList[1].Name);
    }

    [Fact]
    public async Task GetModelAsync_ReturnsSingleModelWithOriginalFileName()
    {
        // Arrange: Service returns a model with original file name
        var mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        var modelId = Guid.NewGuid();
        var expectedModel = new Model3DDto
        {
            Id = modelId,
            FileName = "9f8e7d6c-detailed.stl", // Storage name
            Name = "spaceship-v2-final-REAL-final.stl", // Original name
            FileType = "stl",
            FileSize = 2048,
            Description = "Test model"
        };
        mockService.Setup(s => s.GetModelAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModel);

        var controller = CreateController(mockService);

        // Act: Get single model
        var result = await controller.GetModelAsync(modelId);

        // Assert: Name contains original file name
        var okResult = Assert.IsType<OkObjectResult>(result);
        var model = Assert.IsType<Model3DDto>(okResult.Value);

        Assert.Equal("spaceship-v2-final-REAL-final.stl", model.Name);
        Assert.NotEqual(model.FileName, model.Name);
    }

    [Fact]
    public async Task UploadModelAsync_PreservesOriginalFileName()
    {
        // Arrange: Mock upload that generates GUID storage name but preserves original
        var mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        var uploadId = Guid.NewGuid();
        var originalFileName = "user-uploaded-dragon.stl";

        var uploadResult = new Model3DUploadResultDto
        {
            Id = uploadId,
            FileName = $"{uploadId}.stl", // GUID-based storage
            Name = originalFileName, // Preserved original
            FileType = "stl",
            FileSize = 4096,
            UploadedAt = DateTime.UtcNow
        };

        mockService.Setup(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadResult);

        var controller = CreateController(mockService);
        var formFile = new FormFile(
            new MemoryStream(Encoding.UTF8.GetBytes("STL-DATA")),
            0,
            8,
            "file",
            originalFileName);

        // Act: Upload model
        var result = await controller.UploadModelAsync(formFile);

        // Assert: Upload result contains original file name
        var createdResult = Assert.IsType<CreatedResult>(result);
        var resultDto = Assert.IsType<Model3DUploadResultDto>(createdResult.Value);

        Assert.Equal(originalFileName, resultDto.Name);
        Assert.NotEqual(originalFileName, resultDto.FileName); // Storage name is different
        Assert.StartsWith(uploadId.ToString(), resultDto.FileName); // Storage uses GUID
    }

    [Fact]
    public async Task ListModelsAsync_EmptyName_FallsBackToFileName()
    {
        // Arrange: Edge case - Name is null or empty
        var mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        var expectedModels = new List<Model3DDto>
        {
            new Model3DDto
            {
                Id = Guid.NewGuid(),
                FileName = "legacy-model.stl",
                Name = null, // Legacy data without Name
                FileType = "stl",
                FileSize = 1024
            }
        };
        mockService.Setup(s => s.ListModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedModels);

        var controller = CreateController(mockService);

        // Act
        var result = await controller.ListModelsAsync();

        // Assert: Frontend should handle null Name gracefully
        var okResult = Assert.IsType<OkObjectResult>(result);
        var models = Assert.IsAssignableFrom<IEnumerable<Model3DDto>>(okResult.Value);
        var model = models.First();

        Assert.Null(model.Name);
        Assert.NotNull(model.FileName);
    }

    [Fact]
    public async Task QueryModelsAsync_PreservesOriginalFileNamesInBulkResults()
    {
        // Arrange: Query endpoint returns paginated results
        var mockService = new Mock<IModel3DFileService>(MockBehavior.Strict);
        var searchRequest = new Model3DSearchRequestDto { Search = "test", Page = 1, PageSize = 10 };

        var queryResult = new Model3DListResponse(
            Files: new List<Model3DEntryDto>
            {
                new Model3DEntryDto(
                    Path: "/models/test1.stl",
                    FileName: "abc123-storage.stl",
                    FileSize: 1024,
                    UploadedAt: DateTime.UtcNow,
                    IsDirectory: false,
                    ThumbnailUrl: null,
                    Id: Guid.NewGuid().ToString(),
                    DirectoryId: null,
                    Name: "test-print-1.stl", // Original name in Name field
                    FileType: "stl"
                ),
                new Model3DEntryDto(
                    Path: "/models/test2.stl",
                    FileName: "def456-storage.stl",
                    FileSize: 2048,
                    UploadedAt: DateTime.UtcNow,
                    IsDirectory: false,
                    ThumbnailUrl: null,
                    Id: Guid.NewGuid().ToString(),
                    DirectoryId: null,
                    Name: "test-print-2.stl",
                    FileType: "stl"
                )
            },
            TotalFiles: 2,
            TotalSize: 3072,
            Page: 1,
            PageSize: 10,
            TotalPages: 1,
            TotalItems: 2
        );

        mockService.Setup(s => s.QueryAsync(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<Guid[]?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(queryResult);

        var controller = CreateController(mockService);

        // Act
        var result = await controller.QueryModelsAsync(searchRequest, CancellationToken.None);

        // Assert: All entries preserve original file names
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<Model3DListResponse>(okResult.Value);

        Assert.All(response.Files, entry =>
        {
            Assert.NotNull(entry.Name);
            Assert.NotEqual(entry.FileName, entry.Name);
            Assert.StartsWith("test-print-", entry.Name);
        });
    }
}
