using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Diagnostics;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterMonitoringTests
{
    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData("Offline", false, false, true)]
    [InlineData("Disconnected", false, false, true)]
    [InlineData("Unknown", false, false, false)]
    [InlineData("Error", false, false, false)]
    [InlineData("Offline", true, false, false)]
    [InlineData("Offline", false, true, false)]
    [InlineData("Idle", false, false, false)]
    public void ExpectedShutdown_ExemptsOnlyDisconnectedIdlePrinters(
        string? state, bool activeJob, bool disconnectedWhilePrinting, bool expected)
    {
        Printer printer = new() { ExpectedOfflineSinceUtc = DateTime.UtcNow.AddMinutes(-1) };
        PrinterStatusCacheSnapshot? snapshot = state is null ? null : new(
            new PrinterStatusDto(printer.Id, state == "Idle", state),
            DateTime.UtcNow, HasActivePrintAtDisconnect: disconnectedWhilePrinting);

        PrinterMonitoringPolicy.IsExpectedOffline(printer, snapshot, activeJob).Should().Be(expected);
        printer.ExpectedOfflineSinceUtc = null;
        PrinterMonitoringPolicy.IsExpectedOffline(printer, snapshot, activeJob).Should().BeFalse();
    }

    [Fact]
    public void ReplayedOldOnlineOrUnprovenSamples_CannotClearNewAcknowledgement()
    {
        Printer printer = new() { ExpectedOfflineSinceUtc = DateTime.UtcNow.AddMinutes(-1) };
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(printer.Id, true, "Idle")
        {
            ObservationStartedAtUtc = printer.ExpectedOfflineSinceUtc.Value.AddSeconds(-1),
        });
        cache.UpdateStatus(new PrinterStatusDto(printer.Id, true, "Idle"));
        cache.UpdateStatus(new PrinterStatusDto(printer.Id, false, "Offline"));

        PrinterMonitoringPolicy.HasReconnected(printer.ExpectedOfflineSinceUtc.Value, cache.GetSnapshot(printer.Id)).Should().BeFalse();
        PrinterMonitoringPolicy.IsExpectedOffline(printer, cache.GetSnapshot(printer.Id), false).Should().BeTrue();
    }

    [Fact]
    public void FreshReconnectFollowedByOutage_RetainsEvidenceAndDoesNotMuteNewOutage()
    {
        Printer printer = new() { ExpectedOfflineSinceUtc = DateTime.UtcNow.AddMinutes(-1) };
        PrinterStatusCache cache = CreateCache();
        DateTime observed = DateTime.UtcNow.AddSeconds(-1);
        cache.UpdateStatuses([new PrinterStatusDto(printer.Id, true, "Idle") { ObservationStartedAtUtc = observed }]);
        cache.UpdateStatus(new PrinterStatusDto(printer.Id, false, "Offline"));

        cache.GetSnapshot(printer.Id)!.LastOnlineObservationStartedAtUtc.Should().Be(observed);
        PrinterMonitoringPolicy.HasReconnected(printer.ExpectedOfflineSinceUtc.Value, cache.GetSnapshot(printer.Id)).Should().BeTrue();
        PrinterMonitoringPolicy.IsExpectedOffline(printer, cache.GetSnapshot(printer.Id), false).Should().BeFalse();
    }

    [Fact]
    public void ActivePrintDisconnect_RemainsProtectedAcrossOfflinePollsUntilReconnect()
    {
        Guid id = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(id, true, "Printing"));
        cache.UpdateStatus(new PrinterStatusDto(id, false, "Offline"));
        cache.UpdateStatuses([new PrinterStatusDto(id, false, "Offline")]);
        cache.GetSnapshot(id)!.HasActivePrintAtDisconnect.Should().BeTrue();
        cache.UpdateStatus(new PrinterStatusDto(id, true, "Idle"));
        cache.GetSnapshot(id)!.HasActivePrintAtDisconnect.Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_PersistsClearedIntentAfterFreshReconnectAndLaterOutage()
    {
        PrinterStatusCache cache = CreateCache();
        ServiceCollection services = new();
        string databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IPrinterStatusCacheReader>(cache);
        services.AddSingleton(Mock.Of<IPrinterCacheInvalidator>());
        Mock<IClientProxy> proxy = new();
        proxy.Setup(client => client.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(value => value.Clients.Group(It.IsAny<string>())).Returns(proxy.Object);
        services.AddSingleton(hub.Object);
        services.AddSingleton(Mock.Of<IAttentionBroadcaster>());
        services.AddScoped<PrinterMonitoringService>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        Guid id;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer printer = new() { Name = "Synthetic", ExpectedOfflineSinceUtc = DateTime.UtcNow.AddMinutes(-1) };
            db.Printers.Add(printer);
            await db.SaveChangesAsync();
            id = printer.Id;
        }

        PrinterMonitoringReconnectService reconciler = new(
            provider.GetRequiredService<IServiceScopeFactory>(), cache,
            NullLogger<PrinterMonitoringReconnectService>.Instance);
        await reconciler.ReconcileAsync(CancellationToken.None);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Printers.SingleAsync()).ExpectedOfflineSinceUtc.Should().NotBeNull();
        }

        cache.UpdateStatus(new PrinterStatusDto(id, true, "Idle") { ObservationStartedAtUtc = DateTime.UtcNow.AddSeconds(-1) });
        cache.UpdateStatus(new PrinterStatusDto(id, false, "Offline"));
        await reconciler.ReconcileAsync(CancellationToken.None);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            Printer persisted = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Printers.SingleAsync();
            persisted.ExpectedOfflineSinceUtc.Should().BeNull();
            PrinterMonitoringPolicy.IsExpectedOffline(persisted, cache.GetSnapshot(id), false).Should().BeFalse();
        }

        proxy.Verify(client => client.SendCoreAsync("printermonitoringchanged", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static PrinterStatusCache CreateCache() => new(
        NullLogger<PrinterStatusCache>.Instance, Mock.Of<IDiagnosticChannelService>());
}
