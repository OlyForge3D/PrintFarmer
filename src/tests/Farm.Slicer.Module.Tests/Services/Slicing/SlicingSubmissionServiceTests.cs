using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services.Slicing;

/// <summary>
/// Unit tests for SlicingSubmissionService
/// </summary>
public class SlicingSubmissionServiceTests : IDisposable
{
    private readonly Mock<IUnitOfWork> _mockUnitOfWork;
    private readonly Mock<IModel3DFileRepository> _mockModel3dRepository;
    private readonly Mock<ISlicerFileStorage> _mockFileStorage;
    private readonly Mock<ISlicerOrchestrator> _mockOrchestrator;
    private readonly Mock<IHostEnvironment> _mockEnvironment;
    private readonly ILogger<SlicingSubmissionService> _logger;
    private readonly Mock<ISlicerStoredFileOpsService> _mockFileOperations;
    private readonly SlicingSubmissionService _service;
    private readonly List<string> _tempFiles = new();

    public SlicingSubmissionServiceTests()
    {
        _mockUnitOfWork = new Mock<IUnitOfWork>();
        _mockModel3dRepository = new Mock<IModel3DFileRepository>();
        _mockFileStorage = new Mock<ISlicerFileStorage>();
        _mockOrchestrator = new Mock<ISlicerOrchestrator>();
        _mockEnvironment = new Mock<IHostEnvironment>();
        _logger = NullLogger<SlicingSubmissionService>.Instance;
        _mockFileOperations = new Mock<ISlicerStoredFileOpsService>();

        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Production");

        // Setup the file operations mock to return URLs when requested
        _mockFileOperations
            .Setup(f => f.BuildSlicerJobGcodeUrl(It.IsAny<Guid>()))
            .Returns((Guid jobId) => $"/api/slicer/jobs/{jobId}/gcode");

        _service = new SlicingSubmissionService(
            _mockUnitOfWork.Object,
            _mockModel3dRepository.Object,
            _mockFileStorage.Object,
            _mockOrchestrator.Object,
            _mockEnvironment.Object,
            _logger,
            _mockFileOperations.Object);
    }

    public void Dispose()
    {
        // Cleanup temp files
        foreach (string file in _tempFiles)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullModelRepository_ThrowsArgumentNullException()
    {
        // Act & Assert
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            new SlicingSubmissionService(null!, _mockModel3dRepository.Object, _mockFileStorage.Object, _mockOrchestrator.Object, _mockEnvironment.Object, _logger, _mockFileOperations.Object)
        );
        Assert.Equal("unitOfWork", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithNullFileStorage_ThrowsArgumentNullException()
    {
        // Act & Assert
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            new SlicingSubmissionService(_mockUnitOfWork.Object, _mockModel3dRepository.Object, null!, _mockOrchestrator.Object, _mockEnvironment.Object, _logger, _mockFileOperations.Object)
        );
        Assert.Equal("fileStorage", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithNullOrchestrator_ThrowsArgumentNullException()
    {
        // Act & Assert
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            new SlicingSubmissionService(_mockUnitOfWork.Object, _mockModel3dRepository.Object, _mockFileStorage.Object, null!, _mockEnvironment.Object, _logger, _mockFileOperations.Object)
        );
        Assert.Equal("orchestrator", ex.ParamName);
    }

    #endregion

    #region SubmitSlicingJobAsync Tests

    [Fact]
    public async Task SubmitSlicingJobAsync_WithValidFile_ReturnsSuccess()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("test.stl", 1024);
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/models/test.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued,
                QueuePosition = 1
            });

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobAsync(
            mockFile, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Result);
        Assert.NotEmpty(result.Result.JobId);
        Assert.Equal("Queued", result.Result.Status);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithValidFile_UploadsFileToStorage()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("model.stl", 2048);
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        string capturedKey = string.Empty;
        Stream? capturedStream = null;
        string capturedContentType = string.Empty;

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, CancellationToken>((key, stream, contentType, ct) =>
            {
                capturedKey = key;
                capturedStream = stream;
                capturedContentType = contentType;
            })
            .ReturnsAsync("http://storage.local/uploaded.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued
            });

        // Act
        await _service.SubmitSlicingJobAsync(
            mockFile, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.NotEmpty(capturedKey);
        Assert.Contains("model.stl", capturedKey);
        Assert.NotNull(capturedStream);
        Assert.Equal("application/octet-stream", capturedContentType);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithValidFile_SubmitsToOrchestrator()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("test.stl", 1024);
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        SlicingJobRequest? capturedRequest = null;

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/test.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SlicingJobRequest, CancellationToken>((req, ct) => capturedRequest = req)
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued
            });

        // Act
        await _service.SubmitSlicingJobAsync(
            mockFile, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal(userId, capturedRequest.UserId);
        Assert.Equal(printerId, capturedRequest.PrinterId);
        Assert.Equal("test.stl", capturedRequest.ModelFileName);
        Assert.Equal("http://storage.local/test.stl", capturedRequest.ModelFileUrl.ToString());
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WhenStorageFails_ReturnsFailure()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("test.stl", 1024);
        SlicerProfileDto profile = CreateTestProfile();

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Storage service unavailable"));

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobAsync(
            mockFile, "OrcaSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WhenOrchestratorFails_ReturnsFailure()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("test.stl", 1024);
        SlicerProfileDto profile = CreateTestProfile();

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/test.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No workers available"));

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobAsync(
            mockFile, "OrcaSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_InTestingEnvironment_StoresJobAndReturnsGcodeUrl()
    {
        // Arrange
        _mockEnvironment.SetupGet(e => e.EnvironmentName).Returns("Testing");

        IFormFile mockFile = CreateMockFormFile("test.stl", 512);
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        const string uploadedUrl = "http://storage.local/test.stl";

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadedUrl);

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = jobId,
                Status = SlicingJobStatus.Queued,
                QueuePosition = 0
            });

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobAsync(
            mockFile, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal($"/api/slicer/jobs/{jobId}/gcode", result.Result?.GcodeUrl);
        Assert.Equal(SlicingJobStatus.Queued.ToString(), result.Result?.Status);

        var storedJob = SlicingJobStore.Get(Guid.Parse(jobId.ToString()));
        Assert.NotNull(storedJob);
        Assert.Equal(uploadedUrl, storedJob!.ModelFilePath);
        Assert.Equal(printerId, storedJob.PrinterId);
        Assert.Equal("OrcaSlicer", storedJob.SlicerEngine);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithInvalidSlicerEngine_ReturnsFailureAndLogsError()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("bad.stl", 256);
        SlicerProfileDto profile = CreateTestProfile();

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/bad.stl");

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobAsync(
            mockFile, "NotARealSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SubmitSlicingJobAsync_WithPrusaSlicer_SetsPrusaVersionMetadata()
    {
        // Arrange
        IFormFile mockFile = CreateMockFormFile("prusamodel.stl", 512);
        SlicerProfileDto profile = CreateTestProfile();

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/prusa.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued,
                QueuePosition = 1
            });

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobAsync(
            mockFile, "PrusaSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("PrusaSlicer 2.7.0", result.Result?.Metadata?.SlicerVersion);
    }

    #endregion

    #region SubmitSlicingJobFromModelAsync Tests

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithValidModel_ReturnsSuccess()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("test-model.stl", 1024);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "test-model.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/models/test-model.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued
            });

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Result);
        Assert.NotEmpty(result.Result.JobId);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithNonexistentModel_ReturnsFailure()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        SlicerProfileDto profile = CreateTestProfile();

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync((Model3D?)null);

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WhenModelFileMissing_ReturnsFailure()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "missing.stl",
            FilePath = "/nonexistent/path/missing.stl",
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithValidModel_UploadsFileToStorage()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("model.stl", 2048);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "model.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        string capturedKey = string.Empty;

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, CancellationToken>((key, stream, contentType, ct) =>
            {
                capturedKey = key;
            })
            .ReturnsAsync("http://storage.local/uploaded.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued
            });

        // Act
        await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.NotEmpty(capturedKey);
        Assert.Contains("model.stl", capturedKey);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WhenStorageFails_ReturnsFailure()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("model.stl", 1024);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "model.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Storage unavailable"));

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithValidModel_CallsRepositoryGetById()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("model.stl", 1024);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "model.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        bool repositoryCalled = false;

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .Callback<Guid, CancellationToken>((id, ct) => repositoryCalled = true)
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/model.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued
            });

        // Act
        await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.True(repositoryCalled);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithValidModel_HandlesModelFileCorrectly()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("actual-model.stl", 4096);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "actual-model.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        long streamLength = 0;

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, CancellationToken>((key, stream, contentType, ct) => streamLength = stream.Length)
            .ReturnsAsync("http://storage.local/actual-model.stl");

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = Guid.NewGuid(),
                Status = SlicingJobStatus.Queued
            });

        // Act
        await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.Equal(4096, streamLength);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_InTestingEnvironment_StoresJobAndReturnsGcodeUrl()
    {
        // Arrange
        _mockEnvironment.SetupGet(e => e.EnvironmentName).Returns("Testing");

        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("testing-model.stl", 1024);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "testing-model.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();
        Guid printerId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        const string uploadedUrl = "http://storage.local/testing-model.stl";

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(uploadedUrl);

        _mockOrchestrator
            .Setup(o => o.SubmitJobAsync(It.IsAny<SlicingJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicingJobResponse
            {
                JobId = jobId,
                Status = SlicingJobStatus.Queued,
                QueuePosition = 0
            });

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobFromModelAsync(
            modelId, "OrcaSlicer", printerId, profile, userId, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal($"/api/slicer/jobs/{jobId}/gcode", result.Result?.GcodeUrl);
        Assert.Equal(SlicingJobStatus.Queued.ToString(), result.Result?.Status);

        var storedJob = SlicingJobStore.Get(Guid.Parse(jobId.ToString()));
        Assert.NotNull(storedJob);
        Assert.Equal(uploadedUrl, storedJob!.ModelFilePath);
        Assert.Equal(printerId, storedJob.PrinterId);
        Assert.Equal("OrcaSlicer", storedJob.SlicerEngine);
        Assert.NotNull(storedJob.Profile);
    }

    [Fact]
    public async Task SubmitSlicingJobFromModelAsync_WithInvalidSlicerEngine_ReturnsFailureAndLogsError()
    {
        // Arrange
        Guid modelId = Guid.NewGuid();
        string tempFile = CreateTempFile("invalid-engine.stl", 512);
        Model3D model = new Model3D
        {
            Id = modelId,
            FileName = "invalid-engine.stl",
            FilePath = tempFile,
            UploadedByUserId = Guid.NewGuid()
        };
        SlicerProfileDto profile = CreateTestProfile();

        _mockModel3dRepository
.Setup(r => r.GetByIdAsync(modelId, It.IsAny<CancellationToken>()))
    .ReturnsAsync(model);

        _mockFileStorage
            .Setup(f => f.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://storage.local/invalid-engine.stl");

        // Act
        SlicingSubmissionResult result = await _service.SubmitSlicingJobFromModelAsync(
            modelId, "BadSlicer", Guid.NewGuid(), profile, Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    #endregion

    #region Helper Methods

    private static IFormFile CreateMockFormFile(string fileName, long length)
    {
        byte[] content = new byte[length];
        RandomNumberGenerator.Fill(content);
        MemoryStream ms = new MemoryStream(content);

        Mock<IFormFile> mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.Length).Returns(length);
        mockFile.Setup(f => f.OpenReadStream()).Returns(ms);
        mockFile.Setup(f => f.ContentType).Returns("application/octet-stream");
        mockFile.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, CancellationToken>((stream, ct) =>
            {
                ms.Position = 0;
                return ms.CopyToAsync(stream, ct);
            });

        return mockFile.Object;
    }

    private static SlicerProfileDto CreateTestProfile()
    {
        return new SlicerProfileDto
        {
            MachineProfile = new MachineProfileDto
            {
                Name = "Test Printer",
                Manufacturer = "Test Co"
            },
            ProcessProfile = new ProcessProfileDto
            {
                Name = "Standard Quality",
                LayerHeight = 0.2
            },
            FilamentProfile = new FilamentProfileDto
            {
                Name = "Generic PLA",
                Material = "PLA",
                NozzleTemperature = 210,
                BedTemperature = 60
            }
        };
    }

    private string CreateTempFile(string fileName, int size)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}_{fileName}");
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        File.WriteAllBytes(tempPath, content);
        _tempFiles.Add(tempPath);
        return tempPath;
    }

    #endregion
}
