using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

public sealed class MoonrakerSubscriptionLifecycleTests
{
    [Fact]
    public async Task Enumeration_PrinterDeleted_CancelsLoopAndKeepsAccumulatorCleared()
    {
        bool printerIsActive = true;
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Deleted printer",
            ServerUrl = "http://127.0.0.1",
            BackendPort = 1,
            Backend = (int)PrinterBackend.Moonraker,
            IsEnabled = true
        };
        Mock<IPrintersRepository> printers = new();
        printers
            .Setup(repository => repository.GetByBackendAsync(
                PrinterBackend.Moonraker,
                It.IsAny<CancellationToken>()))
            .Returns((
                PrinterBackend _,
                CancellationToken _) => Task.FromResult(
                    printerIsActive ? new List<Printer> { printer } : []));
        Mock<IUnitOfWork> unitOfWork = new();
        unitOfWork.SetupGet(work => work.Printers).Returns(printers.Object);

        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => unitOfWork.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();

        var clock = new ManualTimeProvider();
        var accumulator = new ToolheadActivityAccumulator(
            TimeSpan.FromMinutes(1),
            clock);
        using var service = new MoonrakerSubscriptionService(
            new Mock<IHubContext<PrinterHub>>().Object,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MoonrakerSubscriptionService>.Instance,
            new Mock<IHttpClientFactory>().Object,
            new Mock<IPrinterStatusCacheWriter>().Object,
            activityAccumulator: accumulator);
        var loopStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool? guardedLateSampleAccepted = null;
        service.SubscriptionLoopOverride = async (_, token) =>
        {
            loopStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                guardedLateSampleAccepted = service.TrySampleActiveToolTelemetry(
                    printer.Id,
                    activeToolIndex: 0,
                    isPrinting: true);

                // Simulate a frame that passed its cancellation guard before teardown began.
                // Reconciliation must await this work before performing the final reset.
                accumulator.Sample(printer.Id, activeToolIndex: 0, isPrinting: true);
                clock.Advance(TimeSpan.FromSeconds(5));
                accumulator.Sample(printer.Id, activeToolIndex: 0, isPrinting: true);
            }
        };

        await service.EnumerateAndStartSubscriptionsAsync(CancellationToken.None);
        await loopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.TrySampleActiveToolTelemetry(printer.Id, 0, isPrinting: true)
            .Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(10));
        service.TrySampleActiveToolTelemetry(printer.Id, 0, isPrinting: true)
            .Should().BeTrue();
        accumulator.PeekActiveSeconds(printer.Id).RecognizedSeconds.Should().Be(10);

        printerIsActive = false;
        accumulator.Reset(printer.Id);
        await service.EnumerateAndStartSubscriptionsAsync(CancellationToken.None);

        guardedLateSampleAccepted.Should().BeFalse();
        ToolheadActivitySnapshot afterDeletion =
            accumulator.PeekActiveSeconds(printer.Id);
        afterDeletion.ActiveSeconds.Should().BeEmpty();
        afterDeletion.WindowSeconds.Should().Be(0);

        service.TrySampleActiveToolTelemetry(printer.Id, 0, isPrinting: true)
            .Should().BeFalse();
        accumulator.PeekActiveSeconds(printer.Id).ActiveSeconds.Should().BeEmpty();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) =>
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
