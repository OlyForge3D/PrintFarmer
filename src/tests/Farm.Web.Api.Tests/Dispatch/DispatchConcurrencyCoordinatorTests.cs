using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Dispatch;

public sealed class DispatchConcurrencyCoordinatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AcquireCapacityAsync_ConfiguredLimit_BoundsRealInFlightWindow(
        int configuredLimit)
    {
        using DispatchConcurrencyCoordinator coordinator = new();
        var activeLeases = new List<DispatchCapacityLease>();

        for (int index = 0; index < configuredLimit; index++)
        {
            activeLeases.Add(
                await coordinator.AcquireCapacityAsync(
                    configuredLimit,
                    CancellationToken.None));
        }

        coordinator.InFlightCount.Should().Be(configuredLimit);
        Task<DispatchCapacityLease> waiting =
            coordinator.AcquireCapacityAsync(
                configuredLimit,
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        waiting.IsCompleted.Should().BeFalse();

        activeLeases[0].Dispose();
        DispatchCapacityLease replacement =
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.InFlightCount.Should().Be(configuredLimit);

        replacement.Dispose();
        foreach (DispatchCapacityLease lease in activeLeases.Skip(1))
        {
            lease.Dispose();
        }

        coordinator.InFlightCount.Should().Be(0);
    }

    [Fact]
    public void TryClaimPrinter_ConcurrentClaim_RejectedUntilReleased()
    {
        using DispatchConcurrencyCoordinator coordinator = new();
        Guid printerId = Guid.NewGuid();

        coordinator.TryClaimPrinter(printerId).Should().BeTrue();
        coordinator.TryClaimPrinter(printerId).Should().BeFalse();

        coordinator.ReleasePrinter(printerId);

        coordinator.TryClaimPrinter(printerId).Should().BeTrue();
        coordinator.ReleasePrinter(printerId);
    }
}
