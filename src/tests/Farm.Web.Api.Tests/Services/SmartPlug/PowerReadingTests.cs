using Farm.Web.Api.Services.SmartPlug;

namespace Farm.Web.Api.Tests.Services.SmartPlug;

/// <summary>
/// Unit tests for <see cref="PowerReading"/> value type correctness.
/// </summary>
public class PowerReadingTests
{
    [Fact]
    public void PowerReading_WithAllFields_StoresCorrectly()
    {
        PowerReading reading = new(WattsNow: 100.5, TotalKwh: 1.23, Voltage: 230.0, CurrentAmps: 0.437);

        reading.WattsNow.Should().BeApproximately(100.5, 0.001);
        reading.TotalKwh.Should().BeApproximately(1.23, 0.001);
        reading.Voltage.Should().BeApproximately(230.0, 0.001);
        reading.CurrentAmps.Should().BeApproximately(0.437, 0.001);
    }

    [Fact]
    public void PowerReading_WithOptionalFieldsNull_DefaultsToNull()
    {
        PowerReading reading = new(WattsNow: 50.0);

        reading.WattsNow.Should().BeApproximately(50.0, 0.001);
        reading.TotalKwh.Should().BeNull();
        reading.Voltage.Should().BeNull();
        reading.CurrentAmps.Should().BeNull();
    }

    [Fact]
    public void PowerReading_RecordEquality_WorksCorrectly()
    {
        PowerReading a = new(100.0, 1.0, 230.0, 0.5);
        PowerReading b = new(100.0, 1.0, 230.0, 0.5);

        a.Should().Be(b);
    }
}
