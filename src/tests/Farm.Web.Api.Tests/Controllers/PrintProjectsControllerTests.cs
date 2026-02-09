using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.DTOs.Projects;
using Farm.Web.Api.Services.Projects;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PrintProjectsControllerTests
{
    private readonly Mock<IPrintProjectService> _projectServiceMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly PrintProjectsController _controller;

    public PrintProjectsControllerTests()
    {
        _projectServiceMock = new Mock<IPrintProjectService>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _controller = new PrintProjectsController(_projectServiceMock.Object, _loggerMock.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    #region GetProjectsAsync Tests

    [Fact]
    public async Task GetProjectsAsync_ReturnsOkWithProjects()
    {
        // Arrange
        var projects = new List<PrintProjectListDto>
        {
            CreateProjectListDto("Project 1"),
            CreateProjectListDto("Project 2")
        } as IReadOnlyList<PrintProjectListDto>;

        _projectServiceMock
            .Setup(s => s.GetProjectsAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        // Act
        ActionResult<IReadOnlyList<PrintProjectListDto>> result = await _controller.GetProjectsAsync();

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(projects, okResult.Value);
    }

    [Fact]
    public async Task GetProjectsAsync_WithStatusFilter_PassesFilterToService()
    {
        // Arrange
        var status = PrintProjectStatus.InProgress;
        IReadOnlyList<PrintProjectListDto> projects = new List<PrintProjectListDto>();

        _projectServiceMock
            .Setup(s => s.GetProjectsAsync(status, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        // Act
        await _controller.GetProjectsAsync(status);

        // Assert
        _projectServiceMock.Verify(s => s.GetProjectsAsync(status, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProjectsAsync_WithSearchFilter_PassesFilterToService()
    {
        // Arrange
        var search = "voron";
        IReadOnlyList<PrintProjectListDto> projects = new List<PrintProjectListDto>();

        _projectServiceMock
            .Setup(s => s.GetProjectsAsync(null, search, It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);

        // Act
        await _controller.GetProjectsAsync(search: search);

        // Assert
        _projectServiceMock.Verify(s => s.GetProjectsAsync(null, search, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProjectsAsync_WithException_ReturnsProblem()
    {
        // Arrange
        _projectServiceMock
            .Setup(s => s.GetProjectsAsync(null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act
        ActionResult<IReadOnlyList<PrintProjectListDto>> result = await _controller.GetProjectsAsync();

        // Assert
        ObjectResult problemResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, problemResult.StatusCode);
    }

    #endregion

    #region GetProjectAsync Tests

    [Fact]
    public async Task GetProjectAsync_WithExistingProject_ReturnsOk()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var project = CreateProjectDetailDto(projectId, "Test Project");

        _projectServiceMock
            .Setup(s => s.GetProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.GetProjectAsync(projectId);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(project, okResult.Value);
    }

    [Fact]
    public async Task GetProjectAsync_WithNonExistingProject_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _projectServiceMock
            .Setup(s => s.GetProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintProjectDetailDto?)null);

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.GetProjectAsync(projectId);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region CreateProjectAsync Tests

    [Fact]
    public async Task CreateProjectAsync_WithValidRequest_ReturnsCreatedAtAction()
    {
        // Arrange
        var request = new CreatePrintProjectRequest("Test Project", "Description", 2);
        var project = CreateProjectDetailDto(Guid.NewGuid(), "Test Project");

        _projectServiceMock
            .Setup(s => s.CreateProjectAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.CreateProjectAsync(request);

        // Assert
        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(project, createdResult.Value);
        Assert.Equal(nameof(PrintProjectsController.GetProjectAsync), createdResult.ActionName);
    }

    [Fact]
    public async Task CreateProjectAsync_WithEmptyName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreatePrintProjectRequest("");

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.CreateProjectAsync(request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateProjectAsync_WithWhitespaceName_ReturnsBadRequest()
    {
        // Arrange
        var request = new CreatePrintProjectRequest("   ");

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.CreateProjectAsync(request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    #endregion

    #region UpdateProjectAsync Tests

    [Fact]
    public async Task UpdateProjectAsync_WithExistingProject_ReturnsOk()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdatePrintProjectRequest("Updated Name", null, null, null, null, null);
        var project = CreateProjectDetailDto(projectId, "Updated Name");

        _projectServiceMock
            .Setup(s => s.UpdateProjectAsync(projectId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.UpdateProjectAsync(projectId, request);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(project, okResult.Value);
    }

    [Fact]
    public async Task UpdateProjectAsync_WithNonExistingProject_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var request = new UpdatePrintProjectRequest("Updated Name", null, null, null, null, null);

        _projectServiceMock
            .Setup(s => s.UpdateProjectAsync(projectId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintProjectDetailDto?)null);

        // Act
        ActionResult<PrintProjectDetailDto> result = await _controller.UpdateProjectAsync(projectId, request);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region DeleteProjectAsync Tests

    [Fact]
    public async Task DeleteProjectAsync_WithExistingProject_ReturnsNoContent()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _projectServiceMock
            .Setup(s => s.DeleteProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        IActionResult result = await _controller.DeleteProjectAsync(projectId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteProjectAsync_WithNonExistingProject_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        _projectServiceMock
            .Setup(s => s.DeleteProjectAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        IActionResult result = await _controller.DeleteProjectAsync(projectId);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    #endregion

    #region AddFilesToProjectAsync Tests

    [Fact]
    public async Task AddFilesToProjectAsync_WithValidFiles_ReturnsCreatedAtAction()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var files = new List<AddFileToProjectRequest>
        {
            new(Guid.NewGuid(), PrintColorRequirement.Base, null, 1, null)
        };
        var addedFiles = new List<PrintProjectFileDto>
        {
            CreateProjectFileDto(Guid.NewGuid(), "test.gcode")
        } as IReadOnlyList<PrintProjectFileDto>;

        _projectServiceMock
            .Setup(s => s.AddFilesToProjectAsync(projectId, files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(addedFiles);

        // Act
        ActionResult<IReadOnlyList<PrintProjectFileDto>> result = await _controller.AddFilesToProjectAsync(projectId, files);

        // Assert
        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(addedFiles, createdResult.Value);
    }

    [Fact]
    public async Task AddFilesToProjectAsync_WithEmptyFiles_ReturnsBadRequest()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var files = new List<AddFileToProjectRequest>();

        // Act
        ActionResult<IReadOnlyList<PrintProjectFileDto>> result = await _controller.AddFilesToProjectAsync(projectId, files);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddFilesToProjectAsync_WithNonExistingProject_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var files = new List<AddFileToProjectRequest>
        {
            new(Guid.NewGuid(), PrintColorRequirement.Base, null, 1, null)
        };

        _projectServiceMock
            .Setup(s => s.AddFilesToProjectAsync(projectId, files, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Project not found"));

        // Act
        ActionResult<IReadOnlyList<PrintProjectFileDto>> result = await _controller.AddFilesToProjectAsync(projectId, files);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region RemoveFileFromProjectAsync Tests

    [Fact]
    public async Task RemoveFileFromProjectAsync_WithExistingFile_ReturnsNoContent()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        _projectServiceMock
            .Setup(s => s.RemoveFileFromProjectAsync(projectId, fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        IActionResult result = await _controller.RemoveFileFromProjectAsync(projectId, fileId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task RemoveFileFromProjectAsync_WithNonExistingFile_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        _projectServiceMock
            .Setup(s => s.RemoveFileFromProjectAsync(projectId, fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        IActionResult result = await _controller.RemoveFileFromProjectAsync(projectId, fileId);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    #endregion

    #region UpdateProjectFileAsync Tests

    [Fact]
    public async Task UpdateProjectFileAsync_WithExistingFile_ReturnsOk()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var request = new UpdateProjectFileRequest(PrintColorRequirement.Accent, null, 2, null, null, null, null);
        var file = CreateProjectFileDto(fileId, "test.gcode");

        _projectServiceMock
            .Setup(s => s.UpdateProjectFileAsync(projectId, fileId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);

        // Act
        ActionResult<PrintProjectFileDto> result = await _controller.UpdateProjectFileAsync(projectId, fileId, request);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(file, okResult.Value);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_WithNonExistingFile_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var request = new UpdateProjectFileRequest(null, null, null, null, null, null, null);

        _projectServiceMock
            .Setup(s => s.UpdateProjectFileAsync(projectId, fileId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintProjectFileDto?)null);

        // Act
        ActionResult<PrintProjectFileDto> result = await _controller.UpdateProjectFileAsync(projectId, fileId, request);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region MarkFilePrintedAsync Tests

    [Fact]
    public async Task MarkFilePrintedAsync_WithExistingFile_ReturnsOk()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var file = CreateProjectFileDto(fileId, "test.gcode", printedCount: 1);

        _projectServiceMock
            .Setup(s => s.MarkFilePrintedAsync(projectId, fileId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);

        // Act
        ActionResult<PrintProjectFileDto> result = await _controller.MarkFilePrintedAsync(projectId, fileId);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        PrintProjectFileDto returnedFile = Assert.IsType<PrintProjectFileDto>(okResult.Value);
        Assert.Equal(1, returnedFile.PrintedCount);
    }

    [Fact]
    public async Task MarkFilePrintedAsync_WithPrintJobId_PassesJobIdToService()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var printJobId = Guid.NewGuid();
        var file = CreateProjectFileDto(fileId, "test.gcode");

        _projectServiceMock
            .Setup(s => s.MarkFilePrintedAsync(projectId, fileId, printJobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(file);

        // Act
        await _controller.MarkFilePrintedAsync(projectId, fileId, printJobId);

        // Assert
        _projectServiceMock.Verify(s => s.MarkFilePrintedAsync(projectId, fileId, printJobId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkFilePrintedAsync_WithNonExistingFile_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        _projectServiceMock
            .Setup(s => s.MarkFilePrintedAsync(projectId, fileId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintProjectFileDto?)null);

        // Act
        ActionResult<PrintProjectFileDto> result = await _controller.MarkFilePrintedAsync(projectId, fileId);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region GetProjectProgressAsync Tests

    [Fact]
    public async Task GetProjectProgressAsync_WithExistingProject_ReturnsOk()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var progress = CreateProjectProgressDto(projectId, "Test Project");

        _projectServiceMock
            .Setup(s => s.GetProjectProgressAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(progress);

        // Act
        ActionResult<PrintProjectProgressDto> result = await _controller.GetProjectProgressAsync(projectId);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(progress, okResult.Value);
    }

    [Fact]
    public async Task GetProjectProgressAsync_WithNonExistingProject_ReturnsNotFound()
    {
        // Arrange
        var projectId = Guid.NewGuid();

        _projectServiceMock
            .Setup(s => s.GetProjectProgressAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintProjectProgressDto?)null);

        // Act
        ActionResult<PrintProjectProgressDto> result = await _controller.GetProjectProgressAsync(projectId);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region Helper Methods

    private static PrintProjectListDto CreateProjectListDto(string name, Guid? id = null)
    {
        return new PrintProjectListDto(
            Id: id ?? Guid.NewGuid(),
            Name: name,
            Description: "Test description",
            Status: PrintProjectStatus.Open,
            Priority: 2,
            DueDate: null,
            TotalFiles: 5,
            CompletedFiles: 2,
            TotalPrints: 10,
            CompletedPrints: 4,
            CreatedAt: DateTime.UtcNow,
            CompletedAt: null);
    }

    private static PrintProjectDetailDto CreateProjectDetailDto(Guid id, string name)
    {
        return new PrintProjectDetailDto(
            Id: id,
            Name: name,
            Description: "Test description",
            Status: PrintProjectStatus.Open,
            Priority: 2,
            DueDate: null,
            Notes: null,
            Files: new List<PrintProjectFileDto>(),
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            CompletedAt: null);
    }

    private static PrintProjectFileDto CreateProjectFileDto(Guid id, string fileName, int printedCount = 0)
    {
        return new PrintProjectFileDto(
            Id: id,
            GcodeFileId: Guid.NewGuid(),
            FileName: fileName,
            ThumbnailUrl: null,
            ColorRequirement: PrintColorRequirement.Base,
            MaterialRequirement: null,
            PrintCount: 1,
            PrintedCount: printedCount,
            Status: printedCount > 0 ? PrintProjectFileStatus.Printing : PrintProjectFileStatus.Pending,
            SortOrder: 0,
            Notes: null,
            LastPrintedAt: null,
            LastPrintJobId: null);
    }

    private static PrintProjectProgressDto CreateProjectProgressDto(Guid id, string name)
    {
        return new PrintProjectProgressDto(
            ProjectId: id,
            ProjectName: name,
            Status: PrintProjectStatus.InProgress,
            TotalFiles: 5,
            CompletedFiles: 2,
            TotalPrints: 10,
            CompletedPrints: 4,
            ProgressPercent: 40,
            FileProgress: new List<FileProgressDto>
            {
                new(Guid.NewGuid(), "file1.gcode", PrintColorRequirement.Base, PrintProjectFileStatus.Completed, 2, 2, true),
                new(Guid.NewGuid(), "file2.gcode", PrintColorRequirement.Accent, PrintProjectFileStatus.Printing, 3, 1, false)
            });
    }

    #endregion
}
