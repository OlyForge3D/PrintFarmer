using Farm.Infrastructure.Services.Cameras;

namespace Farm.Infrastructure.Tests.Services.Cameras;

/// <summary>
/// Overlapping camera syncs for the same printer must serialize so they cannot both create the same
/// discovered camera (#3329 review R-V-05); different printers stay concurrent.
/// </summary>
public sealed class PrinterCameraSyncGateTests
{
    [Fact]
    public async Task AcquireAsync_SamePrinter_WaitsUntilPreviousHolderReleases()
    {
        var gate = new PrinterCameraSyncGate();
        Guid printerId = Guid.NewGuid();
        Task<IDisposable> second;
        using (await gate.AcquireAsync(printerId, CancellationToken.None))
        {
            second = gate.AcquireAsync(printerId, CancellationToken.None);
            await Task.Delay(50);
            second.IsCompleted.Should().BeFalse();
        }

        using IDisposable acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
        acquired.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_DifferentPrinters_DoNotBlockEachOther()
    {
        var gate = new PrinterCameraSyncGate();
        using IDisposable first = await gate.AcquireAsync(Guid.NewGuid(), CancellationToken.None);

        Task<IDisposable> other = gate.AcquireAsync(Guid.NewGuid(), CancellationToken.None);

        other.IsCompleted.Should().BeTrue();
        (await other).Dispose();
    }

    [Fact]
    public async Task AcquireAsync_DoubleDispose_ReleasesOnlyOnce()
    {
        var gate = new PrinterCameraSyncGate();
        Guid printerId = Guid.NewGuid();
        IDisposable first = await gate.AcquireAsync(printerId, CancellationToken.None);
#pragma warning disable IDISP016, IDISP017 // Deliberate double dispose: the releaser must be idempotent.
        first.Dispose();
        first.Dispose();
#pragma warning restore IDISP016, IDISP017

        using IDisposable second = await gate.AcquireAsync(printerId, CancellationToken.None);
        Task<IDisposable> third = gate.AcquireAsync(printerId, CancellationToken.None);

        third.IsCompleted.Should().BeFalse();
    }
}
