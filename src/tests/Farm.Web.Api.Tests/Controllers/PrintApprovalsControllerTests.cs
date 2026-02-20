using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PrintJobs;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Data.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PrintApprovalsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly IPrintApprovalRepository _repository;
    private readonly TestPrintApprovalService _service;
    private readonly PrintApprovalsController _controller;

    public PrintApprovalsControllerTests()
    {
        // Create in-memory SQLite database
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _repository = new EfPrintApprovalRepository(_context);
        _service = new TestPrintApprovalService();
        _controller = new PrintApprovalsController(_service, _repository);
    }

    [Fact]
    public async Task GetPendingAsync_ShouldReturnAllPendingApprovals()
    {
        // Arrange
        // Create valid PrintJobs with proper foreign key chain
        PrintJob printJob1 = await CreateValidPrintJobAsync();
        PrintJob printJob2 = await CreateValidPrintJobAsync();

        var approval1 = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = printJob1.Id,
            PrinterId = Guid.NewGuid(),
            RequestedBy = "user1",
            CreatedAt = DateTime.UtcNow
        };
        var approval2 = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = printJob2.Id,
            RequestedBy = "user2",
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(approval1);
        await _repository.AddAsync(approval2);

        // Act
        IActionResult result = await _controller.GetPendingAsync();

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        IEnumerable<object> approvals = okResult.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        approvals.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetPendingAsync_WhenNoApprovals_ShouldReturnEmptyList()
    {
        // Act
        IActionResult result = await _controller.GetPendingAsync();

        // Assert
        OkObjectResult okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        IEnumerable<object> approvals = okResult.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        approvals.Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveAsync_WithValidId_ShouldReturnNoContent()
    {
        // Arrange
        var approvalId = Guid.NewGuid();
        _service.ApproveResult = true;

        // Act
        IActionResult result = await _controller.ApproveAsync(approvalId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        _service.ApprovedIds.Should().ContainSingle().Which.Should().Be(approvalId);
    }

    [Fact]
    public async Task ApproveAsync_WhenServiceFails_ShouldReturnNotFound()
    {
        // Arrange
        var approvalId = Guid.NewGuid();
        _service.ApproveResult = false;

        // Act
        IActionResult result = await _controller.ApproveAsync(approvalId);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task RejectAsync_WithValidId_ShouldRemoveApprovalAndReturnNoContent()
    {
        // Arrange
        // Create valid PrintJob with proper foreign key chain
        PrintJob printJob = await CreateValidPrintJobAsync();

        var approval = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = printJob.Id,
            RequestedBy = "user1",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(approval);

        // Act
        IActionResult result = await _controller.RejectAsync(approval.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        PrintApproval? removed = await _repository.GetAsync(approval.Id);
        removed.Should().BeNull();
    }

    [Fact]
    public async Task RejectAsync_WithNonExistentId_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        IActionResult result = await _controller.RejectAsync(nonExistentId);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        _context?.Dispose();
        _connection?.Dispose();
    }

    /// <summary>
    /// Helper method to create a valid PrintJob with proper foreign key chain (FolderNode → GcodeFile → PrintJob)
    /// </summary>
    private async Task<PrintJob> CreateValidPrintJobAsync()
    {
        // Create FolderNode (required for GcodeFile)
        string folderPath = $"/test/{Guid.NewGuid()}";
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = folderPath,
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow
        };
        _context.Set<FolderNode>().Add(folder);
        await _context.SaveChangesAsync();

        // Create GcodeFile (required for PrintJob)
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = $"test_{Guid.NewGuid()}.gcode",
            FileName = $"test_{Guid.NewGuid()}.gcode",
            FolderId = folder.Id,
            FilePath = "/tmp/test.gcode",
            FileHash = Guid.NewGuid().ToString(),
            FileSizeBytes = 1000,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.GcodeFiles.Add(gcodeFile);
        await _context.SaveChangesAsync();

        // Create PrintJob with all required fields
        var printJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"Test Job {Guid.NewGuid()}",
            GcodeFileId = gcodeFile.Id,
            Status = PrintJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.PrintJobs.Add(printJob);
        await _context.SaveChangesAsync();

        return printJob;
    }

    // Test double for IPrintApprovalService
    private class TestPrintApprovalService : IPrintApprovalService
    {
        public bool ApproveResult { get; set; } = true;
        public List<Guid> ApprovedIds { get; } = new();

        public Task<Guid> CreatePendingApprovalAsync(Guid printJobId, Guid? printerId, string? requestedBy)
        {
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<bool> ApproveAsync(Guid approvalId, string? approvedBy)
        {
            ApprovedIds.Add(approvalId);
            return Task.FromResult(ApproveResult);
        }
    }
}
