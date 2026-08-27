using Farm.Infrastructure;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Diagnostics;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterStatusCacheProvenanceTests
{
    [Fact]
    public void SnapshotReplay_PreservesProducerWatermark()
    {
        Guid printerId = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(printerId, true, "Idle"), originWatermark: 42);

        PrinterStatusCacheSnapshot first = cache.GetSnapshot(printerId)!;
        PrinterStatusCacheSnapshot replay = cache.GetSnapshot(printerId)!;
        PrinterStatusCacheSnapshot allSnapshot = cache.GetAllSnapshots()[printerId];

        first.OriginWatermark.Should().Be(42);
        replay.Should().Be(first);
        allSnapshot.Should().Be(first);
        cache.GetAllStatuses()[printerId].Should().Be(first.Status);
    }

    [Fact]
    public void SpoolOnlyUpdate_PreservesExistingOriginAndObservationTime()
    {
        Guid printerId = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(printerId, true, "Printing"), originWatermark: 17);
        PrinterStatusCacheSnapshot before = cache.GetSnapshot(printerId)!;
        var spool = new PrinterSpoolInfoDto(
            HasActiveSpool: true,
            ActiveSpoolId: 1,
            SpoolName: "PLA",
            Material: "PLA",
            ColorHex: "#ffffff",
            Vendor: "Vendor",
            RemainingWeightG: 100);

        _ = cache.UpdateSpoolInfo(printerId, spool);

        PrinterStatusCacheSnapshot after = cache.GetSnapshot(printerId)!;
        after.OriginWatermark.Should().Be(17);
        after.UpdatedAtUtc.Should().Be(before.UpdatedAtUtc);
        after.Status.SpoolInfo.Should().Be(spool);
    }

    [Fact]
    public void SyntheticAndUnprovenSnapshots_CarryNullOrigin()
    {
        Guid observedId = Guid.NewGuid();
        Guid syntheticId = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();

        cache.UpdateStatus(new PrinterStatusDto(observedId, false, "Offline"));
        _ = cache.UpdateSpoolInfo(
            syntheticId,
            new PrinterSpoolInfoDto(
                HasActiveSpool: true,
                ActiveSpoolId: 2,
                SpoolName: "PETG",
                Material: "PETG",
                ColorHex: "#000000",
                Vendor: "Vendor",
                RemainingWeightG: 200));

        cache.GetSnapshot(observedId)!.OriginWatermark.Should().BeNull();
        cache.GetSnapshot(syntheticId)!.OriginWatermark.Should().BeNull();
    }

    [Fact]
    public void Combine_UsesMinimumAndFailsClosedOnMissingInput()
    {
        OriginWatermark.Combine(8, 3, 5).Should().Be(3);
        OriginWatermark.Combine(8, null, 5).Should().BeNull();
        OriginWatermark.Combine().Should().BeNull();
    }

    private static PrinterStatusCache CreateCache()
    {
        var diagnostics = new Mock<IDiagnosticChannelService>(MockBehavior.Loose);
        var broadcaster = new Mock<IAttentionBroadcaster>(MockBehavior.Loose);
        broadcaster
            .Setup(value => value.NotifyChangedAsync(
                It.IsAny<AttentionChangedPayload>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PrinterStatusCache(
            NullLogger<PrinterStatusCache>.Instance,
            diagnostics.Object,
            broadcaster.Object);
    }
}
