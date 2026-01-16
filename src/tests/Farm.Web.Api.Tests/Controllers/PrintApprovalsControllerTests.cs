using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Data.Repositories;
using Farm.Web.Api.Services.PrintJobs;
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

        var options = new DbContextOptionsBuilder<AppDbContext>()
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
        var approval1 = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            RequestedBy = "user1",
            CreatedAt = DateTime.UtcNow
        };
        var approval2 = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = Guid.NewGuid(),
            RequestedBy = "user2",
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(approval1);
        await _repository.AddAsync(approval2);

        // Act
        var result = await _controller.GetPendingAsync();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var approvals = okResult.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        approvals.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetPendingAsync_WhenNoApprovals_ShouldReturnEmptyList()
    {
        // Act
        var result = await _controller.GetPendingAsync();

        // Assert
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var approvals = okResult.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        approvals.Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveAsync_WithValidId_ShouldReturnNoContent()
    {
        // Arrange
        var approvalId = Guid.NewGuid();
        _service.ApproveResult = true;

        // Act
        var result = await _controller.ApproveAsync(approvalId);

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
        var result = await _controller.ApproveAsync(approvalId);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task RejectAsync_WithValidId_ShouldRemoveApprovalAndReturnNoContent()
    {
        // Arrange
        var approval = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = Guid.NewGuid(),
            RequestedBy = "user1",
            CreatedAt = DateTime.UtcNow
        };
        await _repository.AddAsync(approval);

        // Act
        var result = await _controller.RejectAsync(approval.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var removed = await _repository.GetAsync(approval.Id);
        removed.Should().BeNull();
    }

    [Fact]
    public async Task RejectAsync_WithNonExistentId_ShouldReturnNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _controller.RejectAsync(nonExistentId);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        _context?.Dispose();
        _connection?.Dispose();
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
