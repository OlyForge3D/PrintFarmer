using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Attention;

public sealed class OfflineAttentionSourceTests
{
    [Fact]
    public async Task OutOfServicePrintersAreExcludedWhileInServiceOutagesRemainVisible()
    {
        Printer outOfService = new() { Id = Guid.NewGuid(), Name = "Out of service", IsEnabled = false };
        Printer active = new() { Id = Guid.NewGuid(), Name = "Active", IsEnabled = true };
        Printer unexpected = new() { Id = Guid.NewGuid(), Name = "Unexpected", IsEnabled = true };
        Mock<IPrintersService> printers = new();
        printers.Setup(service => service.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([outOfService, active, unexpected]);
        Mock<IPrinterStatusCacheReader> cache = new();
        cache.Setup(reader => reader.GetStatus(It.IsAny<Guid>())).Returns((Guid id) => new PrinterStatusDto(id, false, "Offline"));
        Mock<IPrinterMonitoringReader> monitoring = new();
        monitoring.Setup(reader => reader.GetSnapshotsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new(outOfService, new(new PrinterStatusDto(outOfService.Id, false, "Offline"), DateTime.UtcNow), false),
                new(active, new(new PrinterStatusDto(active.Id, false, "Offline"), DateTime.UtcNow), true),
                new(unexpected, new(new PrinterStatusDto(unexpected.Id, false, "Offline"), DateTime.UtcNow), false),
            ]);

        OfflineAttentionSource source = new(printers.Object, cache.Object, monitoring.Object);
        var items = await source.GetItemsAsync(CancellationToken.None);

        items.Select(item => item.PrinterId).Should().BeEquivalentTo([active.Id, unexpected.Id]);
        items.Single(item => item.PrinterId == active.Id).Title.Should().Be("Printer offline with active work");
        items.Single(item => item.PrinterId == active.Id).Detail.Should().Contain("Review the job");
        items.Should().OnlyContain(item => item.OccurredAt == OfflineAttentionSource.StableOfflineOccurredAt);
    }
}
