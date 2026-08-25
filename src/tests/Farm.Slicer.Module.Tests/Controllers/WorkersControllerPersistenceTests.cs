using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Contracts;
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
/// The same defect #1536 fixed for <see cref="WorkersController.DeleteAsync"/> — returning a
/// success result while the change lived only in the EF Core change tracker — survived in every
/// other mutating worker endpoint. <c>EfWorkerRepository</c> mutates tracked entities and relies
/// on the caller to persist, and only <c>DeleteAsync</c> ever called <c>SaveChangesAsync</c>.
///
/// Disable is the sharpest case: the administrative ban is a security control, and the endpoint
/// reported 204 while discarding it when the request's scoped DbContext was disposed. Reset was
/// the loudest: it also released stuck jobs back to the queue and returned a
/// <c>releasedJobs</c> count that was simply untrue.
///
/// Each test clears the change tracker before asserting, so a tracked-but-unsaved mutation
/// cannot satisfy it.
/// </summary>
public sealed class WorkersControllerPersistenceTests
{
    [Fact(DisplayName = "Disabling a worker actually persists the ban")]
    public async Task DisableAsync_PersistsTheDisable()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        Mock<ISliceJobRepository> jobRepository = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        WorkersController controller = CreateController(db, jobRepository);

        Guid workerId = Guid.NewGuid();
        _ = db.Set<Worker>().Add(CreateWorker(workerId));
        _ = await db.SaveChangesAsync();

        IActionResult result = await controller.DisableAsync(
            workerId,
            new DisableWorkerRequest { Reason = "Banned by administrator: producing scrap" },
            CancellationToken.None);

        _ = result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        Worker? persisted = await db.Set<Worker>().FindAsync(workerId);
        _ = persisted.Should().NotBeNull();
        _ = persisted!.IsDisabled.Should().BeTrue("the ban must be persisted, not just tracked in-memory");
        _ = persisted.DisabledReason.Should().Be("Banned by administrator: producing scrap");
    }

    [Fact(DisplayName = "Enabling a worker actually persists the re-enable")]
    public async Task EnableAsync_PersistsTheEnable()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        Mock<ISliceJobRepository> jobRepository = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        WorkersController controller = CreateController(db, jobRepository);

        Guid workerId = Guid.NewGuid();
        Worker worker = CreateWorker(workerId);
        worker.IsDisabled = true;
        worker.DisabledReason = "Banned by administrator: producing scrap";
        worker.DisableSource = WorkerDisableSource.Administrator;
        _ = db.Set<Worker>().Add(worker);
        _ = await db.SaveChangesAsync();

        IActionResult result = await controller.EnableAsync(workerId, CancellationToken.None);

        _ = result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        Worker? persisted = await db.Set<Worker>().FindAsync(workerId);
        _ = persisted.Should().NotBeNull();
        _ = persisted!.IsDisabled.Should().BeFalse("the re-enable must be persisted");
        _ = persisted.DisabledReason.Should().BeNull();
    }

    [Fact(DisplayName = "Updating worker slots actually persists the new value")]
    public async Task UpdateSlotsAsync_PersistsTheNewSlotCount()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();
        Mock<ISliceJobRepository> jobRepository = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        WorkersController controller = CreateController(db, jobRepository);

        Guid workerId = Guid.NewGuid();
        _ = db.Set<Worker>().Add(CreateWorker(workerId));
        _ = await db.SaveChangesAsync();

        IActionResult result = await controller.UpdateSlotsAsync(
            workerId,
            new UpdateWorkerSlotsRequest { TotalSlots = 7 },
            CancellationToken.None);

        _ = result.Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        Worker? persisted = await db.Set<Worker>().FindAsync(workerId);
        _ = persisted.Should().NotBeNull();
        _ = persisted!.TotalSlots.Should().Be(7, "the slot change must be persisted");
    }

    [Fact(DisplayName = "Resetting a worker actually persists the counter reset and released jobs")]
    public async Task ResetAsync_PersistsResetAndReleasedJobs()
    {
        using SlicerDbContext db = TestHelpers.CreateSqliteInMemoryDb();

        Guid workerId = Guid.NewGuid();
        Worker worker = CreateWorker(workerId);
        worker.ActiveJobs = 2;
        _ = db.Set<Worker>().Add(worker);

        SliceJob stuck = new()
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            WorkerId = workerId,
            ProgressPercent = 42,
            ProgressMessage = "slicing",
        };
        _ = db.Set<SliceJob>().Add(stuck);
        _ = await db.SaveChangesAsync();

        Mock<ISliceJobRepository> jobRepository = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        _ = jobRepository
            .Setup(repo => repo.GetJobsByWorkerIdAsync(workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SliceJob> { stuck });

        WorkersController controller = CreateController(db, jobRepository);

        IActionResult result = await controller.ResetAsync(workerId, CancellationToken.None);

        _ = result.Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        Worker? persistedWorker = await db.Set<Worker>().FindAsync(workerId);
        _ = persistedWorker.Should().NotBeNull();
        _ = persistedWorker!.ActiveJobs.Should().Be(0, "the counter reset must be persisted");

        SliceJob? persistedJob = await db.Set<SliceJob>().FindAsync(stuck.Id);
        _ = persistedJob.Should().NotBeNull();
        _ = persistedJob!.Status.Should().Be(
            SliceJobStatus.Queued,
            "the released job must be persisted, or the reported releasedJobs count is untrue");
        _ = persistedJob.WorkerId.Should().BeNull();
    }

    private static WorkersController CreateController(
        SlicerDbContext db,
        Mock<ISliceJobRepository> jobRepository)
    {
        return new WorkersController(
            new EfWorkerRepository(db),
            jobRepository.Object,
            NullLogger<WorkersController>.Instance);
    }

    private static Worker CreateWorker(Guid id)
    {
        DateTime now = DateTime.UtcNow;
        return new Worker
        {
            Id = id,
            ServiceId = Guid.NewGuid().ToString(),
            Name = "persistence-test-worker",
            EndpointUrl = "http://persistence-test-worker.internal",
            Status = WorkerStatus.Online,
            TotalSlots = 1,
            ActiveJobs = 0,
            LastHeartbeat = now,
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            ApiKey = "persistence-test-key",
        };
    }
}
