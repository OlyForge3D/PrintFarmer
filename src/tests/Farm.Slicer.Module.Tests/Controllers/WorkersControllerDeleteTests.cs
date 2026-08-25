using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Regression coverage for #1536: deleting a worker returned <see cref="NoContentResult"/>
/// without the underlying row actually being removed, because
/// <c>EfWorkerRepository.DeleteAsync</c> only marks the entity as removed in the EF Core
/// change tracker and relies on the caller to persist the change via
/// <c>SaveChangesAsync</c>. <see cref="WorkersController.DeleteAsync"/> previously never
/// called it, so the worker silently reappeared on the next fetch.
/// </summary>
public sealed class WorkersControllerDeleteTests
{
    [Fact(DisplayName = "Deleting a worker actually removes it from the database")]
    public async Task DeleteAsync_RemovesWorkerFromDatabase()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        EfWorkerRepository workerRepository = new EfWorkerRepository(db);
        Mock<ISliceJobRepository> jobRepository = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        WorkersController controller = new WorkersController(
            workerRepository,
            jobRepository.Object,
            NullLogger<WorkersController>.Instance);

        Guid workerId = Guid.NewGuid();
        Worker worker = CreateWorker(workerId);
        _ = db.Set<Worker>().Add(worker);
        _ = await db.SaveChangesAsync();

        IActionResult result = await controller.DeleteAsync(workerId, CancellationToken.None);

        _ = result.Should().BeOfType<NoContentResult>();

        // Force a fresh read from the database instead of the tracked in-memory instance so
        // the assertion catches the bug: without SaveChangesAsync, the removal is only in the
        // change tracker and this query would still return the "deleted" worker.
        db.ChangeTracker.Clear();
        Worker? persisted = await db.Set<Worker>().FindAsync(workerId);
        _ = persisted.Should().BeNull("the delete must be persisted, not just tracked in-memory");
    }

    [Fact(DisplayName = "Deleting a nonexistent worker returns NotFound")]
    public async Task DeleteAsync_UnknownWorker_ReturnsNotFound()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        EfWorkerRepository workerRepository = new EfWorkerRepository(db);
        Mock<ISliceJobRepository> jobRepository = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        WorkersController controller = new WorkersController(
            workerRepository,
            jobRepository.Object,
            NullLogger<WorkersController>.Instance);

        IActionResult result = await controller.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        _ = result.Should().BeOfType<NotFoundResult>();
    }

    private static Worker CreateWorker(Guid id)
    {
        DateTime now = DateTime.UtcNow;
        return new Worker
        {
            Id = id,
            ServiceId = Guid.NewGuid().ToString(),
            Name = "delete-test-worker",
            EndpointUrl = "http://delete-test-worker.internal",
            Status = WorkerStatus.Online,
            TotalSlots = 1,
            ActiveJobs = 0,
            LastHeartbeat = now,
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            ApiKey = "delete-test-key",
        };
    }
}
