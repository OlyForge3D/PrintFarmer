using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Data.Repositories;
using Farm.Web.Api.Services.PrintJobQueue;
using Farm.Web.Api.Services.PrintJobs;
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

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _repository = new EfPrintApprovalRepository(_context);
        _queueService = new TestPrintJobQueueService();
        _service = new EfPrintApprovalService(_repository, _queueService);
    }

    [Fact]
    public async Task CreatePendingApprovalAsync_ShouldCreateApprovalInDatabase()
    {
        // Arrange
        var printJobId = Guid.NewGuid();
        var printerId = Guid.NewGuid();
        const string requestedBy = "testuser";

        // Act
        var approvalId = await _service.CreatePendingApprovalAsync(printJobId, printerId, requestedBy);

        // Assert
        approvalId.Should().NotBeEmpty();
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().NotBeNull();
        approval!.PrintJobId.Should().Be(printJobId);
        approval.PrinterId.Should().Be(printerId);
        approval.RequestedBy.Should().Be(requestedBy);
        approval.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreatePendingApprovalAsync_WithNullPrinter_ShouldCreateApproval()
    {
        // Arrange
        var printJobId = Guid.NewGuid();
        const string requestedBy = "testuser";

        // Act
        var approvalId = await _service.CreatePendingApprovalAsync(printJobId, null, requestedBy);

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
        var printJobId = Guid.NewGuid();
        var printerId = Guid.NewGuid();
        var approvalId = await _service.CreatePendingApprovalAsync(printJobId, printerId, "testuser");

        // Act
        var result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue();
        _queueService.EnqueuedRequests.Should().ContainSingle();
        _queueService.EnqueuedRequests[0].gcodeFileId.Should().Be(printJobId);
        _queueService.EnqueuedRequests[0].assignedPrinterId.Should().Be(printerId);

        // Approval should be removed after successful enqueue
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
        var printJobId = Guid.NewGuid();
        var approvalId = await _service.CreatePendingApprovalAsync(printJobId, null, "testuser");
        _queueService.ShouldFailEnqueue = true;

        // Act
        var result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeFalse();

        // Approval should still exist since enqueue failed
        var approval = await _repository.GetAsync(approvalId);
        approval.Should().NotBeNull();
    }

    [Fact]
    public async Task ApproveAsync_WithNullPrinterId_ShouldEnqueueWithoutPrinterAssignment()
    {
        // Arrange
        var printJobId = Guid.NewGuid();
        var approvalId = await _service.CreatePendingApprovalAsync(printJobId, null, "testuser");

        // Act
        var result = await _service.ApproveAsync(approvalId, "approver");

        // Assert
        result.Should().BeTrue();
        _queueService.EnqueuedRequests.Should().ContainSingle();
        _queueService.EnqueuedRequests[0].assignedPrinterId.Should().BeNull();
    }

    [Fact]
    public async Task ListPendingAsync_ShouldReturnAllPendingApprovals()
    {
        // Arrange
        var approval1Id = await _service.CreatePendingApprovalAsync(Guid.NewGuid(), Guid.NewGuid(), "user1");
        var approval2Id = await _service.CreatePendingApprovalAsync(Guid.NewGuid(), null, "user2");
        var approval3Id = await _service.CreatePendingApprovalAsync(Guid.NewGuid(), Guid.NewGuid(), "user3");

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
        var approval1Id = await _service.CreatePendingApprovalAsync(Guid.NewGuid(), Guid.NewGuid(), "user1");
        var approval2Id = await _service.CreatePendingApprovalAsync(Guid.NewGuid(), null, "user2");

        // Approve one
        await _service.ApproveAsync(approval1Id, "approver");

        // Act
        var pending = await _repository.ListPendingAsync();

        // Assert
        pending.Should().ContainSingle();
        pending.Should().Contain(a => a.Id == approval2Id);
        pending.Should().NotContain(a => a.Id == approval1Id);
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

        public Task<PrintJobDto?> EnqueueAsync(EnqueuePrintJobRequest request, CancellationToken ct = default)
        {
            if (ShouldFailEnqueue)
            {
                return Task.FromResult<PrintJobDto?>(null);
            }

            EnqueuedRequests.Add(request);
            return Task.FromResult<PrintJobDto?>(new PrintJobDto(
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

        public Task<IEnumerable<PrintJobDto>> GetAllAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<PrintJobDto?> GetAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
