// SPDX-License-Identifier: AGPL-3.0-only
using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.Electricity;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Electricity;

public class PowerReadingPruneServiceTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExecuteAsync_ShutdownDuringPausedDelay_ExitsWithoutError()
    {
        using var stopping = new CancellationTokenSource();
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var logger = new Mock<ILogger<PowerReadingPruneService>>();
        var fence = new PowerReadingPruneFenceFlag();
        await fence.RequestPauseAsync(CancellationToken.None);
        using var service = new TestablePowerReadingPruneService(scopeFactory.Object, logger.Object, fence);

        // Run inline until the paused delay yields, rather than racing BackgroundService.StartAsync.
        Task execution = service.RunAsync(stopping.Token);
        stopping.Cancel();
        await execution.WaitAsync(s_testTimeout);

        Assert.True(await fence.IsPausedAsync(CancellationToken.None));
        scopeFactory.Verify(factory => factory.CreateScope(), Times.Never);
        VerifyNoErrors(logger);
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownDuringDelete_ExitsWithoutError()
    {
        using var stopping = new CancellationTokenSource(s_testTimeout);
        var interceptor = new CancelPruneCommandInterceptor(stopping);
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite("Data Source=:memory:").AddInterceptors(interceptor));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var logger = new Mock<ILogger<PowerReadingPruneService>>();
        using var service = new TestablePowerReadingPruneService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger.Object);

        try
        {
            await service.RunAsync(stopping.Token).WaitAsync(s_testTimeout);
        }
        finally
        {
            stopping.Cancel();
        }

        Assert.Equal(stopping.Token, interceptor.ObservedToken);
        VerifyNoErrors(logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NonShutdownFailure_LogsErrorAndKeepsRunning(bool isCancellation)
    {
        using var stopping = new CancellationTokenSource();
        Exception failure = isCancellation
            ? new OperationCanceledException("Unrelated cancellation")
            : new InvalidOperationException("Prune failed");
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        scopeFactory.Setup(factory => factory.CreateScope()).Throws(failure);
        var logger = new Mock<ILogger<PowerReadingPruneService>>();
        using var service = new TestablePowerReadingPruneService(scopeFactory.Object, logger.Object);

        Task execution = service.RunAsync(stopping.Token);
        bool keptRunning = !execution.IsCompleted;
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(s_testTimeout));

        Assert.True(keptRunning);
        scopeFactory.Verify(factory => factory.CreateScope(), Times.Once);
        logger.Verify(
            item => item.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString() == "PowerReadingPruneService: error during prune"),
                failure,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static void VerifyNoErrors(Mock<ILogger<PowerReadingPruneService>> logger)
    {
        logger.Verify(
            item => item.Log(
                It.Is<LogLevel>(level => level >= LogLevel.Error),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private sealed class TestablePowerReadingPruneService(
        IServiceScopeFactory scopeFactory,
        ILogger<PowerReadingPruneService> logger,
        PowerReadingPruneFenceFlag? fence = null) : PowerReadingPruneService(scopeFactory, logger, fence)
    {
        public Task RunAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    private sealed class CancelPruneCommandInterceptor(CancellationTokenSource stopping) : DbCommandInterceptor
    {
        public CancellationToken? ObservedToken { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            stopping.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
