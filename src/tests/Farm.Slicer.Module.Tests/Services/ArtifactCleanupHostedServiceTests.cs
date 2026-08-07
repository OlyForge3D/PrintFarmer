using Farm.Slicer.Module.Api.Services;
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class ArtifactCleanupHostedServiceTests
{
    [Theory]
    [InlineData(1194)]
    [InlineData(8760)]
    [InlineData(int.MaxValue)]
    public async Task NormalizeCleanupIntervalHours_OversizedValue_ReturnsTaskDelayCompatibleInterval(
        int configuredHours)
    {
        int normalizedHours =
            ArtifactCleanupHostedService.NormalizeCleanupIntervalHours(
                configuredHours);
        TimeSpan interval = TimeSpan.FromHours(normalizedHours);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> scheduleDelay = async () =>
            await Task.Delay(interval, cancellation.Token);

        normalizedHours.Should().Be(24 * 7);
        await scheduleDelay.Should().ThrowAsync<OperationCanceledException>();
    }
}
