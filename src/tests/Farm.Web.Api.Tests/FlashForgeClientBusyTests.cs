using Farm.Backend.Plugin.FlashForge;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests that FlashForgeClient detects firmware-level busy (BUILDING/BUILDING_FROM_SD)
/// and propagates it as <see cref="PrinterBackendBusyException"/> (#317).
///
/// Full integration tests (StartPrintAsync → M23 rejection → M119 status check → exception)
/// require a TCP mock server and are covered by manual QA or a future Docker integration test.
/// These unit tests validate the detection logic that drives the exception path.
/// </summary>
public class FlashForgeClientBusyTests
{
    [Theory]
    [InlineData("CMD M119 Received.\r\nMachineStatus: BUILDING_FROM_SD\r\nMoveMode: READY\r\nok\r\n")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: BUILDING\r\nMoveMode: READY\r\nok\r\n")]
    public void IsBuildingStatus_WhenMachineStatusIsBuilding_ReturnsTrue(string m119Response)
    {
        bool result = FlashForgeClient.IsBuildingStatus(m119Response);
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("CMD M119 Received.\r\nMachineStatus: READY\r\nMoveMode: READY\r\nok\r\n")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: PAUSED\r\nMoveMode: PAUSED\r\nok\r\n")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: BUILDING_COMPLETED\r\nMoveMode: READY\r\nok\r\n")]
    [InlineData("")]
    public void IsBuildingStatus_WhenMachineStatusIsNotBuilding_ReturnsFalse(string m119Response)
    {
        bool result = FlashForgeClient.IsBuildingStatus(m119Response);
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("CMD M119 Received.\r\nMachineStatus: BUILDING_FROM_SD\r\nok\r\n", "Printing")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: BUILDING\r\nok\r\n", "Printing")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: READY\r\nok\r\n", "Idle")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: PAUSED\r\nok\r\n", "Paused")]
    [InlineData("CMD M119 Received.\r\nMachineStatus: BUILDING_COMPLETED\r\nok\r\n", "Complete")]
    public void ParseMachineStatus_MapsKnownStatesToExpectedValues(string m119Response, string expectedState)
    {
        string? state = FlashForgeClient.ParseMachineStatus(m119Response);
        state.Should().Be(expectedState);
    }
}
