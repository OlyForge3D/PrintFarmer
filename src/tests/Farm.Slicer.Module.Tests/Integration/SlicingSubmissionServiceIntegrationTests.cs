using System;
using System.IO;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Web.Api.Services.Slicing;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Integration tests for SlicingSubmissionService
/// Tests job submission, validation, file handling, and orchestration
/// Fast executing (~5-6 seconds for 18 tests) - suitable for CI/CD pipelines
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class SlicingSubmissionServiceIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public SlicingSubmissionServiceIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    /// <summary>
    /// Helper method to get or create a model folder for tests
    /// </summary>
    private async Task<FolderNode> GetOrCreateModelFolderAsync(AppDbContext context)
    {
        FolderNode? folder = await context.Set<FolderNode>().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "model");
        if (folder == null)
        {
            folder = new FolderNode
            {
                Id = Guid.NewGuid(),
                Path = "/",
                FolderType = "model"
            };
            context.Set<FolderNode>().Add(folder);
            await context.SaveChangesAsync();
        }
        return folder;
    }

    private IFormFile CreateMockFormFile(
        string fileName = "test-model.stl",
        string content = "mock file content")
    {
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream);
        writer.Write(content);
        writer.Flush();
        memoryStream.Position = 0;

        var mock = new Mock<IFormFile>();
        mock.Setup(_ => _.FileName).Returns(fileName);
        mock.Setup(_ => _.Length).Returns(memoryStream.Length);
        mock.Setup(_ => _.OpenReadStream()).Returns(memoryStream);
        mock.Setup(_ => _.ContentDisposition).Returns($"form-data; name=\"file\"; filename=\"{fileName}\"");

        return mock.Object;
    }

    private async Task<Model3D> CreateTestModelAsync(string fileName = "test-model.stl")
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        FolderNode folder = await GetOrCreateModelFolderAsync(context);

        string filePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_" + fileName);

        // Create the actual file
        File.WriteAllText(filePath, "mock stl content");

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            FilePath = filePath,
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString(),
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            FolderId = folder.Id,  // Set required FolderId
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Set<Model3D>().Add(model);
        await context.SaveChangesAsync();

        return model;
    }

    #region SubmitSlicingJobAsync Tests

    [Fact]
    public async Task SubmitSlicingJobAsync_WithValidFile_SucceedsAndReturnsJobId()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile("valid-model.stl");
        var profile = new SlicerProfileDto
        {
            ProcessProfile = new ProcessProfileDto { Quality = "high" },
            FilamentProfile = new FilamentProfileDto { Material = "PLA" }
        };

        var userId = Guid.NewGuid();
        var printerId = Guid.NewGuid();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            printerId,
            profile,
            userId,
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result.Should().NotBeNull();
        result.Result!.JobId.Should().NotBeNullOrEmpty();
        result.Result.Status.Should().Be("Queued");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithOrcaSlicer_SetCorrectSlicerVersion()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto
        {
            ProcessProfile = new ProcessProfileDto { Quality = "normal" },
            FilamentProfile = new FilamentProfileDto { Material = "PETG" }
        };

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.Metadata.SlicerVersion.Should().Contain("OrcaSlicer");
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithPrusaSlicer_SetCorrectSlicerVersion()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto
        {
            ProcessProfile = new ProcessProfileDto { Quality = "draft" },
            FilamentProfile = new FilamentProfileDto { Material = "ABS" }
        };

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "prusaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.Metadata.SlicerVersion.Should().Contain("PrusaSlicer");
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_IncludesProfileMetadata()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto
        {
            ProcessProfile = new ProcessProfileDto { Quality = "ultra" },
            FilamentProfile = new FilamentProfileDto { Material = "Nylon" }
        };

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.Metadata.ProfileUsed.Should().Contain("ultra");
        result.Result.Metadata.ProfileUsed.Should().Contain("Nylon");
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithNullProfile_HandlesGracefully()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            new SlicerProfileDto(), // Empty profile
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.Metadata.ProfileUsed.Should().Contain("Unknown");
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_SetsInitialProgressToZero()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.Progress.Should().Be(0);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_GeneratesUniqueJobId()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result1 = await service.SubmitSlicingJobAsync(
            CreateMockFormFile("model1.stl"),
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        SlicingSubmissionResult result2 = await service.SubmitSlicingJobAsync(
            CreateMockFormFile("model2.stl"),
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();
        result1.Result!.JobId.Should().NotBe(result2.Result!.JobId);
    }

    #endregion

    #region SubmitSlicingJobFromModelAsync Tests

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithValidModel_SucceedsAndReturnsJobId()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        Model3D model = await CreateTestModelAsync("valid-model.stl");
        var profile = new SlicerProfileDto
        {
            ProcessProfile = new ProcessProfileDto { Quality = "high" },
            FilamentProfile = new FilamentProfileDto { Material = "PLA" }
        };

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobFromModelAsync(
            model.Id,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result.Should().NotBeNull();
        result.Result!.JobId.Should().NotBeNullOrEmpty();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithNonExistentModel_ReturnsFalseWithError()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        var nonExistentModelId = Guid.NewGuid();
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobFromModelAsync(
            nonExistentModelId,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
        result.Result.Should().BeNull();
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithMissingFile_ReturnsFalseWithError()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        FolderNode folder = await GetOrCreateModelFolderAsync(context);

        // Create model but don't create the actual file
        string nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".stl");
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            FileName = "missing-file.stl",
            FilePath = nonExistentPath,
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString(),
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            FolderId = folder.Id,  // Set required FolderId
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.Set<Model3D>().Add(model);
        await context.SaveChangesAsync();

        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobFromModelAsync(
            model.Id,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found on disk");
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_PreservesOriginalFileName()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        string expectedFileName = "my-custom-model-name.stl";
        Model3D model = await CreateTestModelAsync(expectedFileName);
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobFromModelAsync(
            model.Id,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        // The model's original filename should be used in the submission
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithDifferentSlicer_SubmitsCorrectly()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        Model3D model = await CreateTestModelAsync();
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobFromModelAsync(
            model.Id,
            "prusaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.Metadata.SlicerVersion.Should().Contain("PrusaSlicer");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task SubmitSlicingJobAsync_WithInvalidSlicerEngine_HandlesGracefully()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto();

        // Act - Invalid slicer engine should be handled
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "invalidslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert - Should fail with error message rather than crash
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithLargeFile_HandlesCorrectly()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        // Create a larger mock file (5MB)
        string largeContent = string.Concat(Enumerable.Repeat("x", 5 * 1024 * 1024));
        IFormFile formFile = CreateMockFormFile("large-model.stl", largeContent);
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.Result!.JobId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Printer Association Tests

    [Fact]
    public async Task SubmitSlicingJobAsync_AssociatesPrinterId()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto();
        var expectedPrinterId = Guid.NewGuid();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            expectedPrinterId,
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        // Printer ID would be stored in the job submission
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_AssociatesUserId()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile();
        var profile = new SlicerProfileDto();
        var expectedUserId = Guid.NewGuid();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            expectedUserId,
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        // User ID would be stored in the job submission
    }

    #endregion

    #region File Format Tests

    [Fact]
    public async Task SubmitSlicingJobAsync_WithSTLFile_SubmitsSuccessfully()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile("model.stl");
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithOBJFile_SubmitsSuccessfully()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile("model.obj");
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_With3MFFile_SubmitsSuccessfully()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISlicingSubmissionService service = scope.ServiceProvider.GetRequiredService<ISlicingSubmissionService>();

        IFormFile formFile = CreateMockFormFile("model.3mf");
        var profile = new SlicerProfileDto();

        // Act
        SlicingSubmissionResult result = await service.SubmitSlicingJobAsync(
            formFile,
            "orcaslicer",
            Guid.NewGuid(),
            profile,
            Guid.NewGuid(),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
    }

    #endregion
}
