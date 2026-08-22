using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using IStoredFileOperationsService = Farm.Infrastructure.Services.FileManagement.IStoredFileOperationsService;


namespace Farm.Slicer.Module.Tests.Services;

public class ModelServiceAdditionalTests : IDisposable
{
    private readonly List<MemoryStream> _streamsToDispose = [];

    public void Dispose()
    {
        foreach (MemoryStream stream in _streamsToDispose)
        {
            stream.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private IFormFile CreateFormFile(string name, string content, string fileName)
    {
        MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        _streamsToDispose.Add(ms);
        return new FormFile(ms, 0, ms.Length, name, fileName);
    }

    private static Mock<IFolderManagementService> CreateFolderServiceMock()
    {
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/",
            FolderType = "models"
        };

        var mock = new Mock<IFolderManagementService>(MockBehavior.Strict);
        _ = mock.Setup(f => f.GetOrCreateFolderAsync("/", "models", It.IsAny<CancellationToken>()))
            .ReturnsAsync(folder);
        return mock;
    }

    private static Mock<IStoredFileOperationsService> CreateStoredFileOperationsServiceMock()
    {
        var mock = new Mock<IStoredFileOperationsService>(MockBehavior.Loose);
        mock.Setup(s => s.BuildModel3DThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(modelId => $"/api/3d-models/thumbnail/{modelId}");
        mock.Setup(s => s.GetFullFilePath(It.IsAny<StoredFile>()))
            .Returns<StoredFile>(f => Path.Join(f.FilePath, f.FileName));
        mock.Setup(s => s.GetFullThumbnailPath(It.IsAny<StoredFile>()))
            .Returns<StoredFile>(f => f.ThumbnailFileName != null ? Path.Join(f.FilePath, f.ThumbnailFileName) : null);
        mock.Setup(s => s.GenerateThumbnailFileName(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns<Guid, string>((id, ext) => $"{id}_thumb{ext}");
        return mock;
    }

    [Fact]
    public async Task UploadModelAsync_CompositeHash_Path_CreatesNewHash()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();

        // Arrange: existing model with same file hash but different base name and same extension
        string content = "abc123";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        string contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        Model3D existing = new Model3D
        {
            Id = Guid.NewGuid(),
            FileName = "othername.stl",
            FileHash = contentHash,
            FileFormat = ModelFileFormat.STL,
            IsValid = true,
            UploadedAt = DateTime.UtcNow
        };

        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

        // Wrap the repository in a UnitOfWork mock
        Mock<IUnitOfWork> mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
        mockUoW.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Mock IStoragePathService (like GcodeFilesService does)
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();
        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object);

        IFormFile file = CreateFormFile("file", content, "model.stl");

        Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

        // When base names differ, composite hash should be computed and a new model added
        mockRepo.Verify(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(result);
        // FileName should now be GUID-based (matching GcodeFile pattern)
        Assert.EndsWith(".stl", result.FileName);
    }

    [Fact]
    public void ValidateModel_InvalidFileType_ReturnsIssue()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();
        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>();
        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        // Setup ValidateModelExtension to throw for invalid extensions
        _ = mockFileManagement.Setup(s => s.ValidateModelExtension(It.IsAny<string>()))
            .Callback<string>(ext =>
            {
                string[] allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply", ".step", ".stp" };
                string extension = ext.StartsWith('.') ? ext : "." + ext;
                if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Invalid file type '{extension}'");
                }
            });

        // Wrap the repository in a UnitOfWork mock
        Mock<IUnitOfWork> mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
        mockUoW.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Mock IStoragePathService (like GcodeFilesService does)
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();
        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object);

        IFormFile badFile = CreateFormFile("file", "x", "model.exe");

        Model3DValidationResultDto result = service.ValidateModel(badFile);

        Assert.False(result.Valid);
        Assert.NotNull(result.Issues);
    }

    [Fact]
    public void ValidateModel_EmptyFile_ThrowsArgument()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();
        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>();
        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Wrap the repository in a UnitOfWork mock
        _ = new Mock<IUnitOfWork>(MockBehavior.Loose);

        // Mock IStoragePathService (like GcodeFilesService does)
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();
        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object);

        MemoryStream emptyStream = new();
        _streamsToDispose.Add(emptyStream);
        IFormFile empty = new FormFile(emptyStream, 0, 0, "file", "empty.stl");

        _ = Assert.Throws<ArgumentException>(() => service.ValidateModel(empty));
    }

    [Fact]
    public async Task UploadModelAsync_AnalysisServiceThrows_Succeeds()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();

        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);
        _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> mockAnalysis = new Mock<IModelAnalysisService>(MockBehavior.Strict);
        _ = mockAnalysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("analysis failed"));

        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

        // Wrap the repository in a UnitOfWork mock
        Mock<IUnitOfWork> mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
        mockUoW.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Mock IStoragePathService (like GcodeFilesService does)
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object, mockAnalysis.Object);

        IFormFile file = CreateFormFile("file", "content", "model.stl");

        Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

        Assert.NotNull(result);
        mockRepo.Verify(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadModelAsync_RepositorySaveFails_Propagates()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();

        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);
        _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db failure"));

        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

        // Wrap the repository in a UnitOfWork mock
        Mock<IUnitOfWork> mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
        mockUoW.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db failure"));

        // Mock IStoragePathService (like GcodeFilesService does)
        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object);

        IFormFile file = CreateFormFile("file", "content", "model.stl");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadModelAsync(file, CancellationToken.None));
    }

    /// <summary>
    /// Before #1866, an analysis result with <c>IsValid: false</c> was persisted anyway — the
    /// malformed file was accepted with only <c>IsValid=false</c> metadata, and broke later (e.g.
    /// in the model viewer) instead of being rejected at upload time. STL/OBJ uploads that fail
    /// structural analysis must now be rejected outright.
    /// </summary>
    [Theory]
    [InlineData("model.stl")]
    [InlineData("model.obj")]
    public async Task UploadModelAsync_AnalysisReturnsInvalidForStlOrObj_RejectsUpload(string fileName)
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();

        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);

        Mock<IModelAnalysisService> mockAnalysis = new Mock<IModelAnalysisService>(MockBehavior.Strict);
        _ = mockAnalysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelAnalysisResult(
                null,
                null,
                null,
                0,
                IsValid: false,
                ValidationErrors: ["No triangles found in mesh"]));

        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object, mockAnalysis.Object);

        IFormFile file = CreateFormFile("file", "content", fileName);

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadModelAsync(file, CancellationToken.None));
        Assert.Contains("No triangles found in mesh", ex.Message);

        mockRepo.Verify(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Never);
        mockRepo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The #1866 rejection gate is deliberately scoped to STL/OBJ only. 3MF keeps its established
    /// best-effort behavior from #1814: a structurally-invalid 3MF is still persisted (with
    /// <c>IsValid=false</c> metadata) rather than rejected outright.
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_AnalysisReturnsInvalidFor3mf_StillPersistsAsInvalid()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();

        Model3D? added = null;
        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);
        _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Callback<Model3D, CancellationToken>((m, _) => added = m)
            .Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> mockAnalysis = new Mock<IModelAnalysisService>(MockBehavior.Strict);
        _ = mockAnalysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelAnalysisResult(
                null,
                null,
                null,
                0,
                IsValid: false,
                ValidationErrors: ["No triangles found in mesh"]));

        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object, mockAnalysis.Object);

        IFormFile file = CreateFormFile("file", "content", "model.3mf");

        Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(added);
        Assert.False(added!.IsValid);
        Assert.NotNull(added.ValidationErrors);
        Assert.Contains("No triangles found in mesh", added.ValidationErrors);
    }

    /// <summary>
    /// When analysis returns null (unsupported format, e.g. PLY/STEP), the model must remain
    /// listed (<c>IsValid = true</c>): "not analyzed" must never be inferred as "invalid" — that
    /// would silently hide unrelated formats from the library (#1814).
    /// </summary>
    [Fact]
    public async Task UploadModelAsync_AnalysisReturnsNull_KeepsIsValidTrue()
    {
        IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Mock<ILogger<Model3DFileService>> mockLogger = new Mock<ILogger<Model3DFileService>>();

        Model3D? added = null;
        Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
        _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);
        _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
            .Callback<Model3D, CancellationToken>((m, _) => added = m)
            .Returns(Task.CompletedTask);
        _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> mockAnalysis = new Mock<IModelAnalysisService>(MockBehavior.Strict);
        _ = mockAnalysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelAnalysisResult?)null);

        Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
        _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
            .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

        var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        string tempDir = Path.Join(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
        mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

        Model3DFileService service = new Model3DFileService(mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object, mockAnalysis.Object);

        IFormFile file = CreateFormFile("file", "content", "model.step");

        Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(added);
        Assert.True(added!.IsValid);
        Assert.Null(added.ValidationErrors);
    }
}
