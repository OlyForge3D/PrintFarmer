using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Diagnostics;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterStatusCacheSensorTests
{
    private static PrinterSensorDto Chamber(double temperature, double? humidity = null) =>
        new("temperature_sensor chamber", "Chamber", PrinterSensorKind.Chamber)
        {
            Readings = humidity is double value
                ?
                [
                    new(PrinterSensorQuantity.Temperature, PrinterSensorValue.FromNumber(temperature), PrinterSensorUnit.Celsius),
                    new(PrinterSensorQuantity.Humidity, PrinterSensorValue.FromNumber(value), PrinterSensorUnit.Percent),
                ]
                : [new(PrinterSensorQuantity.Temperature, PrinterSensorValue.FromNumber(temperature), PrinterSensorUnit.Celsius)],
        };

    [Fact]
    public void UpdateSensors_UncachedPrinter_ReturnsNull()
    {
        PrinterStatusCache cache = CreateCache();

        Assert.Null(cache.UpdateSensors(Guid.NewGuid(), [Chamber(30)], replaceAll: true));
    }

    [Fact]
    public void UpdateSensors_SensorOnlyChange_ReturnsUpdatedStatus_AndUnchangedReturnsNull()
    {
        Guid id = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(id, true, "idle"), originWatermark: 7);

        PrinterStatusDto? updated = cache.UpdateSensors(id, [Chamber(30)], replaceAll: true);

        Assert.NotNull(updated);
        Assert.Equal(30, Assert.Single(updated!.Sensors).Readings[0].Value.Number);
        Assert.Equal(7, cache.GetSnapshot(id)!.OriginWatermark);
        Assert.Null(cache.UpdateSensors(id, [Chamber(30)], replaceAll: true));
    }

    [Fact]
    public void UpdateSensors_PartialUpdate_PreservesOmittedReadings()
    {
        Guid id = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(id, true, "idle") { Sensors = [Chamber(30, humidity: 40)] });

        PrinterStatusDto updated = cache.UpdateSensors(id, [Chamber(32)], replaceAll: false)!;

        PrinterSensorDto sensor = Assert.Single(updated.Sensors);
        Assert.Equal(32, sensor.Readings.Single(r => r.Quantity == PrinterSensorQuantity.Temperature).Value.Number);
        Assert.Equal(40, sensor.Readings.Single(r => r.Quantity == PrinterSensorQuantity.Humidity).Value.Number);
    }

    [Fact]
    public void UpdateStatus_WithoutSensors_PreservesCachedSensors()
    {
        Guid id = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(id, true, "idle") { Sensors = [Chamber(30)] });

        cache.UpdateStatus(new PrinterStatusDto(id, true, "printing"));

        Assert.Single(cache.GetStatus(id)!.Sensors);
    }

    [Fact]
    public void UpdateStatus_Offline_MarksSensorsUnavailableButKeepsValues()
    {
        Guid id = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();
        cache.UpdateStatus(new PrinterStatusDto(id, true, "idle") { Sensors = [Chamber(30)] });

        cache.UpdateStatus(new PrinterStatusDto(id, false, "offline"));

        PrinterSensorDto sensor = Assert.Single(cache.GetStatus(id)!.Sensors);
        Assert.False(sensor.Available);
        Assert.Equal(30, sensor.Readings[0].Value.Number);
        Assert.False(Assert.Single(cache.UpdateSensors(id, [Chamber(31)], replaceAll: true)!.Sensors).Available);
    }

    [Fact]
    public void UpdateStatus_NormalizesAndBoundsIncomingSensors()
    {
        Guid id = Guid.NewGuid();
        PrinterStatusCache cache = CreateCache();

        cache.UpdateStatus(new PrinterStatusDto(id, true, "idle")
        {
            Sensors = [Chamber(30), Chamber(31), new PrinterSensorDto("", "blank", PrinterSensorKind.Other)],
        });

        Assert.Equal(31, Assert.Single(cache.GetStatus(id)!.Sensors).Readings[0].Value.Number);
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
