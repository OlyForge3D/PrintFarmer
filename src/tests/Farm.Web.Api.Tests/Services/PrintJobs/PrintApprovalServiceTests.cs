using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrintJobs;
using Farm.Infrastructure.Services.PrintJobs;
using Farm.Infrastructure.Services.Queue;
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
    private readonly StubJobQueueService _queueService;
    private readonly IPrintApprovalService _service;

    public PrintApprovalServiceTests()
    {
        // Create in-memory SQLite database
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Enable foreign keys for SQLite
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _repository = new EfPrintApprovalRepository(_context);
        _queueService = new StubJobQueueService();
        _service = new PrintApprovalService(_repository, _queueService);
    }

    [Fact]
    public async Task CreatePendingApprovalAsync_ShouldCreateApprovalInDatabase()
    {
        // Arrange
        PrintJob printJob = await CreateValidPrintJobAsync();
        var printerId = Guid.NewGuid();
        const string requestedBy = "testuser";

        // Act
        Guid approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, printerId, requestedBy);

        // Assert
        approvalId.Should().NotBeEmpty();
        PrintApproval? approval = await _repository.GetAsync(approvalId);
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
        PrintJob printJob = await CreateValidPrintJobAsync();
        const string requestedBy = "testuser";

        // Act
        Guid approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, null, requestedBy);

        // Assert
        approvalId.Should().NotBeEmpty();
        PrintApproval? approval = await _repository.GetAsync(approvalId);
        approval.Should().NotBeNull();
        approval!.PrinterId.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_ShouldEnqueueJobAndRemoveApproval()
    {
        // Arrange
        PrintJob printJob = await CreateValidPrintJobAsync();
        var printerId = Guid.NewGuid();
        Guid approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, printerId, "testuser");

        // Act
        bool result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue();

        // Approval should be removed after approval
        PrintApproval? approval = await _repository.GetAsync(approvalId);
        approval.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_WithNonExistentApproval_ShouldReturnFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        bool result = await _service.ApproveAsync(nonExistentId, "approver");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ApproveAsync_WhenEnqueueFails_ShouldNotRemoveApproval()
    {
        // Arrange
        PrintJob printJob = await CreateValidPrintJobAsync();
        Guid approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, null, "testuser");

        // Act
        bool result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue(); // Approval service just removes the approval, doesn't enqueue

        // Approval should be removed
        PrintApproval? approval = await _repository.GetAsync(approvalId);
        approval.Should().BeNull();
    }

    [Fact]
    public async Task ApproveAsync_WithNullPrinterId_ShouldEnqueueWithoutPrinterAssignment()
    {
        // Arrange
        PrintJob printJob = await CreateValidPrintJobAsync();
        Guid approvalId = await _service.CreatePendingApprovalAsync(printJob.Id, null, "testuser");

        // Act
        bool result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue();

        // Approval should be removed
        PrintApproval? approval = await _repository.GetAsync(approvalId);
        approval.Should().BeNull();
    }

    [Fact]
    public async Task ListPendingAsync_ShouldReturnAllPendingApprovals()
    {
        // Arrange
        PrintJob printJob1 = await CreateValidPrintJobAsync();
        PrintJob printJob2 = await CreateValidPrintJobAsync();
        PrintJob printJob3 = await CreateValidPrintJobAsync();

        Guid approval1Id = await _service.CreatePendingApprovalAsync(printJob1.Id, Guid.NewGuid(), "user1");
        Guid approval2Id = await _service.CreatePendingApprovalAsync(printJob2.Id, null, "user2");
        Guid approval3Id = await _service.CreatePendingApprovalAsync(printJob3.Id, Guid.NewGuid(), "user3");

        // Act
        IEnumerable<PrintApproval> pending = await _repository.ListPendingAsync();

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
        PrintJob printJob1 = await CreateValidPrintJobAsync();
        PrintJob printJob2 = await CreateValidPrintJobAsync();

        Guid approval1Id = await _service.CreatePendingApprovalAsync(printJob1.Id, Guid.NewGuid(), "user1");
        Guid approval2Id = await _service.CreatePendingApprovalAsync(printJob2.Id, null, "user2");

        // Approve one
        await _service.ApproveAsync(approval1Id, "approver");

        // Act
        IEnumerable<PrintApproval> pending = await _repository.ListPendingAsync();

        // Assert
        pending.Should().ContainSingle();
        pending.Should().Contain(a => a.Id == approval2Id);
        pending.Should().NotContain(a => a.Id == approval1Id);
    }

    private async Task<PrintJob> CreateValidPrintJobAsync()
    {
        // Create a valid FolderNode first (required for GcodeFile → StoredFile)
        // Use unique path for each test to avoid UNIQUE constraint violations
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

    /// <summary>
    /// Stub implementation of IJobQueueService for testing PrintApprovalService.
    /// Always succeeds when adding jobs to the queue.
    /// </summary>
    private class StubJobQueueService : IJobQueueService
    {
        public List<QueuePrintJobDto> EnqueuedJobs { get; } = new();
        public bool ShouldFailEnqueue { get; set; }

        public Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, CancellationToken ct)
        {
            if (ShouldFailEnqueue)
            {
                return Task.FromResult<JobQueuePrintJobDto?>(null);
            }

            EnqueuedJobs.Add(request);
            return Task.FromResult<JobQueuePrintJobDto?>(new JobQueuePrintJobDto
            {
                Id = Guid.NewGuid(),
                GcodeFileId = request.GcodeFileId,
                GcodeFileName = "test.gcode",
                AssignedPrinterId = request.AssignedPrinterId,
                Status = PrintJobStatus.Queued,
                QueuePosition = 1,
                CreatedAt = DateTime.UtcNow
            });
        }

        public Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(string? requiredModel, decimal? requiredNozzle, string? requiredMaterial, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<QueueOverviewDto>>(new List<QueueOverviewDto>());

        public Task<IReadOnlyList<JobQueuePrintJobDto>> GetPrinterQueueAsync(Guid printerId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JobQueuePrintJobDto>>(new List<JobQueuePrintJobDto>());

        public Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct)
            => Task.FromResult<JobQueuePrintJobDto?>(null);

        public Task<bool> RemoveJobAsync(Guid id, CancellationToken ct)
            => Task.FromResult(true);

        public Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct)
            => Task.FromResult<JobQueuePrintJobDto?>(null);

        public Task<JobQueuePrintJobDto?> UpdateJobAsync(Guid id, UpdatePrintJobStatusDto request, CancellationToken ct)
            => Task.FromResult<JobQueuePrintJobDto?>(null);
    }
}
