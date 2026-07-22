using Farm.Backend.Plugin.Sdcp;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests that SdcpClient detects an active print job from CurrentStatus codes and
/// propagates it as <see cref="Farm.Infrastructure.Services.Printers.PrinterBackendBusyException"/> (#317).
///
/// Full integration tests (StartPrintAsync → SendCommandAsync → GetCurrentStatusArrayAsync → exception)
/// require a live WebSocket server; those are covered by manual QA or a future integration test suite.
/// These unit tests validate the detection helper that drives the exception path.
/// </summary>
public class SdcpClientBusyTests
{
    [Theory]
    [InlineData(new[] { 1 })]           // printing
    [InlineData(new[] { 9 })]           // starting (transient)
    [InlineData(new[] { 1, 2 })]        // printing with additional codes
    [InlineData(new[] { 0, 1 })]        // idle + printing (any-match)
    public void IsPrintingStatus_WhenCurrentStatusContainsPrintingCode_ReturnsTrue(int[] currentStatus)
    {
        bool result = SdcpClient.IsPrintingStatus(currentStatus);
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData(new[] { 0 })]           // idle
    [InlineData(new[] { 2 })]           // other error state, not printing
    [InlineData(new[] { 3, 4, 5 })]     // various non-printing codes
    [InlineData(new int[0])]            // empty array
    public void IsPrintingStatus_WhenCurrentStatusHasNoPrintingCode_ReturnsFalse(int[] currentStatus)
    {
        bool result = SdcpClient.IsPrintingStatus(currentStatus);
        result.Should().BeFalse();
    }
}
