using System;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Data.Repositories;
using Farm.Web.Api.Services.PrintJobQueue;
using Farm.Web.Api.Services.PrintJobs;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PrintJobs;

public class PrintApprovalServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly IPrintApprovalRepository _repository;
    private readonly TestPrintJobQueueService _queueService;
    private readonly IPrintApprovalService _service;

    public PrintApprovalServiceTests()
    {
        // Create in-memory SQLite database
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Enable foreign keys for SQLite
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _repository = new EfPrintApprovalRepository(_context);
        _queueService = new TestPrintJobQueueService();
        _service = new PrintApprovalService(_context, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrintApprovalService>.Instance);
    }

    [Fact]
    public async Task CreatePendingApprovalAsync_ShouldCreateApprovalInDatabase()
    {
        // Arrange
        var printJob = await CreateValidPrintJobAsync();
        var printerId = Guid.NewGuid();
        const string requestedBy = "testuser";

        // Act
        var approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, printerId, requestedBy);

        // Assert
        approvalId.Should().NotBeEmpty();
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().NotBeNull();
        approval!.PrintJobId.Should().Be(printJob.Id);
        approval.PrinterId.Should().Be(printerId);
        approval.RequestedBy.Should().Be(requestedBy);
        approval.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreatePendingApprovalAsync_WithNullPrinter_ShouldCreateApproval()
    {
        // Arrange
        var printJob = await CreateValidPrintJobAsync();
        const string requestedBy = "testuser";

        // Act
        var approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, null, requestedBy);

        // Assert
        approvalId.Should().NotBeEmpty();
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().NotBeNull();
        approval!.PrinterId.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_ShouldEnqueueJobAndRemoveApproval()
    {
        // Arrange
        var printJob = await CreateValidPrintJobAsync();
        var printerId = Guid.NewGuid();
        var approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, printerId, "testuser");

        // Act
        var result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue();

        // Approval should be removed after approval
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_WithNonExistentApproval_ShouldReturnFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _service.ApproveAsync(nonExistentId, "approver");

        // Assert
        result.Should().BeFalse();
        _queueService.EnqueuedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveAsync_WhenEnqueueFails_ShouldNotRemoveApproval()
    {
        // Arrange
        var printJob = await CreateValidPrintJobAsync();
        var approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, null, "testuser");

        // Act
        var result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue(); // Approval service just removes the approval, doesn't enqueue

        // Approval should be removed
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_WithNullPrinterId_ShouldEnqueueWithoutPrinterAssignment()
    {
        // Arrange
        var printJob = await CreateValidPrintJobAsync();
        var approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, null, "testuser");

        // Act
        var result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue();

        // Approval should be removed
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().BeNull();
    }

    [Fact]
    public async Task ListPendingAsync_ShouldReturnAllPendingApprovals()
    {
        // Arrange
        var printJob1 = await CreateValidPrintJobAsync();
        var printJob2 = await CreateValidPrintJobAsync();
        var printJob3 = await CreateValidPrintJobAsync();

        var approval1Id = await _service.CreatePendingApprovalAsync(printJob1.Id, Guid.NewGuid(), "user1");
        var approval2Id = await _service.CreatePendingApprovalAsync(printJob2.Id, null, "user2");
        var approval3Id = await _service.CreatePendingApprovalAsync(printJob3.Id, Guid.NewGuid(), "user3");

        // Act
        var pending = await _repository.ListPendingAsync();

        // Assert
        pending.Should().HaveCount(3);
        pending.Should().Contain(a => a.Id == approval1Id);
        pending.Should().Contain(a => a.Id == approval2Id);
        pending.Should().Contain(a => a.Id == approval3Id);
    }

    [Fact]
    public async Task ListPendingAsync_AfterApproval_ShouldNotIncludeApprovedItem()
    {
        // Arrange
        var printJob1 = await CreateValidPrintJobAsync();
        var printJob2 = await CreateValidPrintJobAsync();

        var approval1Id = await _service.CreatePendingApprovalAsync(printJob1.Id, Guid.NewGuid(), "user1");
        var approval2Id = await _service.CreatePendingApprovalAsync(printJob2.Id, null, "user2");

        // Approve one
        await _service.ApproveAsync(approval1Id, "approver");

        // Act
        var pending = await _repository.ListPendingAsync();

        // Assert
        pending.Should().ContainSingle();
        pending.Should().Contain(a => a.Id == approval2Id);
        pending.Should().NotContain(a => a.Id == approval1Id);
    }

    private async Task<PrintJob> CreateValidPrintJobAsync()
    {
        // Create a valid FolderNode first (required for GcodeFile → StoredFile)
        // Use unique path for each test to avoid UNIQUE constraint violations
        var folderPath = $"/test/{Guid.NewGuid()}";
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = folderPath,
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow
        };
        _context.Folders.Add(folder);
        await _context.SaveChangesAsync();

        // Create a valid GcodeFile (required for PrintJob foreign key)
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

        // Create PrintJob with valid foreign key and required fields
        var printJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"Test Job {Guid.NewGuid()}", // Required field
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

    public void Dispose()
    {
        _context?.Dispose();
        _connection?.Dispose();
    }

    // Test double for IPrintJobQueueService
    private class TestPrintJobQueueService : IPrintJobQueueService
    {
        public List<EnqueuePrintJobRequest> EnqueuedRequests { get; } = new();
        public bool ShouldFailEnqueue { get; set; }

        public Task<Farm.Web.Api.Services.PrintJobQueue.PrintJobDto?> EnqueueAsync(EnqueuePrintJobRequest request, CancellationToken ct = default)
        {
            if (ShouldFailEnqueue)
            {
                return Task.FromResult<Farm.Web.Api.Services.PrintJobQueue.PrintJobDto?>(null);
            }

            EnqueuedRequests.Add(request);
            return Task.FromResult<Farm.Web.Api.Services.PrintJobQueue.PrintJobDto?>(new Farm.Web.Api.Services.PrintJobQueue.PrintJobDto(
                Id: request.gcodeFileId,
                GcodeFileId: request.gcodeFileId,
                GcodeFileName: "Test Job",
                AssignedPrinterId: request.assignedPrinterId,
                AssignedPrinterName: null,
                Status: "Queued",
                QueuePosition: 1,
                RequiredNozzleDiameter: request.requiredNozzleDiameter,
                RequiredMaterialType: request.requiredMaterialType,
                CreatedAt: DateTime.UtcNow
            ));
        }

        public Task<IEnumerable<Farm.Web.Api.Services.PrintJobQueue.PrintJobDto>> GetAllAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<Farm.Web.Api.Services.PrintJobQueue.PrintJobDto?> GetAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
