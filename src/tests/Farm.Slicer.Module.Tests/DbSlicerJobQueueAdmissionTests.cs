using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests;

/// <summary>
/// Kane/panel audit (issue #2663, "physical admission barrier is not real"): slicer submission
/// must honor the host-update admission gate the same way <c>JobQueueService.AddJobToQueueAsync</c>
/// already does, or the fence coordinator's proof of quiescence is checking a gate no real
/// producer consults.
/// </summary>
public sealed class DbSlicerJobQueueAdmissionTests
{
    [Fact]
    public async Task EnqueueAsync_GateClosed_ThrowsAndNeverWritesToRepository()
    {
        var repo = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        var gate = new Mock<IHostUpdateAdmissionGate>(MockBehavior.Strict);
        gate.Setup(g => g.IsClosedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var queue = new DbSlicerJobQueue(repo.Object, hostUpdateAdmissionGate: gate.Object);

        Func<Task> act = () => queue.EnqueueAsync(new DistributedSlicingJob(), CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateAdmissionClosedException>();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnqueueAsync_GateOpen_DelegatesToRepository()
    {
        var repo = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var gate = new Mock<IHostUpdateAdmissionGate>(MockBehavior.Strict);
        gate.Setup(g => g.IsClosedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var queue = new DbSlicerJobQueue(repo.Object, hostUpdateAdmissionGate: gate.Object);

        await queue.EnqueueAsync(new DistributedSlicingJob(), CancellationToken.None);

        repo.Verify(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_NoGateConfigured_DelegatesToRepository()
    {
        var repo = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var queue = new DbSlicerJobQueue(repo.Object);

        await queue.EnqueueAsync(new DistributedSlicingJob(), CancellationToken.None);

        repo.Verify(r => r.AddAsync(It.IsAny<SliceJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DequeueAsync_GateClosed_ThrowsAndNeverClaimsRepositoryLease()
    {
        var repo = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        var gate = new Mock<IHostUpdateAdmissionGate>(MockBehavior.Strict);
        gate.Setup(g => g.IsClosedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var queue = new DbSlicerJobQueue(repo.Object, hostUpdateAdmissionGate: gate.Object);

        Func<Task> act = () => queue.DequeueAsync(Guid.NewGuid().ToString(), cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateAdmissionClosedException>();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompleteJobAsync_GateClosed_ThrowsAndNeverWritesCompletion()
    {
        var repo = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        var gate = new Mock<IHostUpdateAdmissionGate>(MockBehavior.Strict);
        gate.Setup(g => g.IsClosedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var queue = new DbSlicerJobQueue(repo.Object, hostUpdateAdmissionGate: gate.Object);
        DistributedSlicingJob job = ClaimedJob();

        Func<Task> act = () => queue.CompleteJobAsync(job, new SlicingResult(), CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateAdmissionClosedException>();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateProgressAsync_GateClosed_ThrowsAndNeverWritesProgress()
    {
        var repo = new Mock<ISliceJobRepository>(MockBehavior.Strict);
        var gate = new Mock<IHostUpdateAdmissionGate>(MockBehavior.Strict);
        gate.Setup(g => g.IsClosedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var queue = new DbSlicerJobQueue(repo.Object, hostUpdateAdmissionGate: gate.Object);

        Func<Task> act = () => queue.UpdateProgressAsync(ClaimedJob(), 50, cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateAdmissionClosedException>();
        repo.VerifyNoOtherCalls();
    }

    private static DistributedSlicingJob ClaimedJob() => new()
    {
        Id = Guid.NewGuid(),
        WorkerId = Guid.NewGuid().ToString(),
        ClaimToken = Guid.NewGuid(),
    };

}
