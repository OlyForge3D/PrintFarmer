using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Harvest;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Tests for GcodeHarvestOperation entity change tracking and persistence
/// Specifically tests that the cancel operation properly persists to the database
/// </summary>
public class HarvestOperationChangeTrackingTests : IAsyncLifetime
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private AppDbContext _dbContext = null!;
    private EfHarvestRepository _harvestRepository = null!;

    public HarvestOperationChangeTrackingTests()
    {
        // Use in-memory database for testing
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
    }

    public async Task InitializeAsync()
    {
        _dbContext = new AppDbContext(_dbOptions);
        _ = await _dbContext.Database.EnsureCreatedAsync();

        _harvestRepository = new EfHarvestRepository(_dbContext);
    }

    public async Task DisposeAsync()
    {
        _ = await _dbContext.Database.EnsureDeletedAsync();
        _dbContext.Dispose();
    }

    [Fact]
    public async Task GetOperationByIdAsync_WithAsNoTracking_ReturnsDetachedEntity()
    {
        // Arrange
        Guid printerId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();

        GcodeHarvestOperation operation = new GcodeHarvestOperation
        {
            Id = operationId,
            PrinterId = printerId,
            Status = GcodeHarvestStatus.Running,
            StartedAt = DateTime.UtcNow,
            CompletedAt = null,
            FilesFound = 0
        };

        await _harvestRepository.AddOperationAsync(operation);
        await _harvestRepository.SaveChangesAsync();

        // Act - Get with AsNoTracking (returns detached entity)
        GcodeHarvestOperation? detachedOp = await _harvestRepository.GetOperationByIdAsync(operationId);

        // Assert - Entity is detached, so modifying and saving won't work
        _ = detachedOp.Should().NotBeNull();
        _ = detachedOp!.Status.Should().Be(GcodeHarvestStatus.Running);

        // Modifying and saving should NOT persist changes
        detachedOp.Status = GcodeHarvestStatus.Cancelled;
        detachedOp.CompletedAt = DateTime.UtcNow;
        await _harvestRepository.SaveChangesAsync();

        // Verify status was NOT saved (entity was detached)
        GcodeHarvestOperation? fetchedOp = await _harvestRepository.GetOperationByIdAsync(operationId);
        _ = fetchedOp!.Status.Should().Be(GcodeHarvestStatus.Running); // Still running!
    }

    [Fact]
    public async Task GetOperationByIdTrackedAsync_ReturnsTrackedEntity()
    {
        // Arrange
        Guid printerId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();

        GcodeHarvestOperation operation = new GcodeHarvestOperation
        {
            Id = operationId,
            PrinterId = printerId,
            Status = GcodeHarvestStatus.Running,
            StartedAt = DateTime.UtcNow,
            CompletedAt = null,
            FilesFound = 0
        };

        await _harvestRepository.AddOperationAsync(operation);
        await _harvestRepository.SaveChangesAsync();

        // Act - Get with tracking enabled
        GcodeHarvestOperation? trackedOp = await _harvestRepository.GetOperationByIdTrackedAsync(operationId);

        // Assert - Entity is tracked, so modifying and saving WILL work
        _ = trackedOp.Should().NotBeNull();
        _ = trackedOp!.Status.Should().Be(GcodeHarvestStatus.Running);

        // Modifying and saving SHOULD persist changes
        trackedOp.Status = GcodeHarvestStatus.Cancelled;
        trackedOp.CompletedAt = DateTime.UtcNow;
        await _harvestRepository.SaveChangesAsync();

        // Verify status WAS saved (entity was tracked)
        GcodeHarvestOperation? fetchedOp = await _harvestRepository.GetOperationByIdAsync(operationId);
        _ = fetchedOp!.Status.Should().Be(GcodeHarvestStatus.Cancelled); // Now cancelled!
        _ = fetchedOp.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelOperation_WithTrackedEntity_PersistsToDatabase()
    {
        // Arrange - Create an operation
        Guid printerId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();

        GcodeHarvestOperation operation = new GcodeHarvestOperation
        {
            Id = operationId,
            PrinterId = printerId,
            Status = GcodeHarvestStatus.Running,
            StartedAt = DateTime.UtcNow,
            CompletedAt = null,
            FilesFound = 0
        };

        await _harvestRepository.AddOperationAsync(operation);
        await _harvestRepository.SaveChangesAsync();

        // Act - Simulate cancel: fetch with tracking, modify, save
        GcodeHarvestOperation? trackedOp = await _harvestRepository.GetOperationByIdTrackedAsync(operationId);
        trackedOp!.Status = GcodeHarvestStatus.Cancelled;
        trackedOp.CompletedAt = DateTime.UtcNow;
        await _harvestRepository.SaveChangesAsync();

        // Assert - Verify persistence in new context to ensure actual DB save
        AppDbContext newContext = new AppDbContext(_dbOptions);
        EfHarvestRepository newRepo = new EfHarvestRepository(newContext);

        GcodeHarvestOperation? persistedOp = await newRepo.GetOperationByIdAsync(operationId);
        _ = persistedOp.Should().NotBeNull();
        _ = persistedOp!.Status.Should().Be(GcodeHarvestStatus.Cancelled);
        _ = persistedOp.CompletedAt.Should().NotBeNull();

        newContext.Dispose();
    }
}
