using Farm.Infrastructure.Services.Printers;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class PrinterStateNormalizerTests
{
    // ── Idle-equivalent states ──────────────────────────────────────────
    [Theory]
    [InlineData("IDLE", "Idle")]
    [InlineData("idle", "Idle")]
    [InlineData("Idle", "Idle")]
    [InlineData("standby", "Idle")]
    [InlineData("Standby", "Idle")]
    [InlineData("STANDBY", "Idle")]
    [InlineData("ready", "Idle")]
    [InlineData("Ready", "Idle")]
    [InlineData("READY", "Idle")]
    [InlineData("operational", "Idle")]
    [InlineData("Operational", "Idle")]
    [InlineData("OPERATIONAL", "Idle")]
    [InlineData("online", "Idle")]
    [InlineData("Online", "Idle")]
    [InlineData("ONLINE", "Idle")]
    [InlineData("unknown", "Idle")]
    public void NormalizeState_IdleEquivalents_ReturnsIdle(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Printing-equivalent states ──────────────────────────────────────
    [Theory]
    [InlineData("PRINTING", "Printing")]
    [InlineData("printing", "Printing")]
    [InlineData("Printing", "Printing")]
    [InlineData("busy", "Printing")]
    [InlineData("BUSY", "Printing")]
    [InlineData("preparing", "Printing")]
    [InlineData("starting", "Printing")]
    public void NormalizeState_PrintingEquivalents_ReturnsPrinting(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Paused ──────────────────────────────────────────────────────────
    [Theory]
    [InlineData("PAUSED", "Paused")]
    [InlineData("paused", "Paused")]
    [InlineData("Paused", "Paused")]
    public void NormalizeState_Paused_ReturnsPaused(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Error-equivalent states ─────────────────────────────────────────
    [Theory]
    [InlineData("ERROR", "Error")]
    [InlineData("error", "Error")]
    [InlineData("Error", "Error")]
    [InlineData("attention", "Error")]
    [InlineData("ATTENTION", "Error")]
    public void NormalizeState_ErrorEquivalents_ReturnsError(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Offline ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData("OFFLINE", "Offline")]
    [InlineData("offline", "Offline")]
    [InlineData("Offline", "Offline")]
    public void NormalizeState_Offline_ReturnsOffline(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Shutdown ─────────────────────────────────────────────────────────
    [Theory]
    [InlineData("shutdown", "Shutdown")]
    [InlineData("Shutdown", "Shutdown")]
    [InlineData("SHUTDOWN", "Shutdown")]
    public void NormalizeState_Shutdown_ReturnsShutdown(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Halted ───────────────────────────────────────────────────────────
    [Theory]
    [InlineData("halted", "Halted")]
    [InlineData("Halted", "Halted")]
    [InlineData("HALTED", "Halted")]
    public void NormalizeState_Halted_ReturnsHalted(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Disconnected ────────────────────────────────────────────────────
    [Theory]
    [InlineData("disconnected", "Disconnected")]
    [InlineData("Disconnected", "Disconnected")]
    [InlineData("DISCONNECTED", "Disconnected")]
    public void NormalizeState_Disconnected_ReturnsDisconnected(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Complete-equivalent states ──────────────────────────────────────
    [Theory]
    [InlineData("complete", "Complete")]
    [InlineData("finished", "Complete")]
    [InlineData("FINISHED", "Complete")]
    [InlineData("stopped", "Complete")]
    public void NormalizeState_CompleteEquivalents_ReturnsComplete(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Cancelled ───────────────────────────────────────────────────────
    [Theory]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("CANCELLED", "Cancelled")]
    public void NormalizeState_Cancelled_ReturnsCancelled(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Connecting ──────────────────────────────────────────────────────
    [Theory]
    [InlineData("CONNECTING", "Connecting")]
    [InlineData("connecting", "Connecting")]
    public void NormalizeState_Connecting_ReturnsConnecting(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    // ── Null/empty handling ─────────────────────────────────────────────
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeState_WithNullOrEmpty_ReturnsNullOrEmpty(string? input)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(input);
    }

    // ── Fallback to PascalCase for unrecognized states ──────────────────
    [Theory]
    [InlineData("custom_state", "Custom_state")]
    [InlineData("SOME_NEW_STATE", "Some_new_state")]
    public void NormalizeState_UnrecognizedState_FallsToPascalCase(string input, string expected)
    {
        PrinterStateNormalizer.NormalizeState(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeState_IsStateless()
    {
        string? result1 = PrinterStateNormalizer.NormalizeState("PRINTING");
        string? result2 = PrinterStateNormalizer.NormalizeState("PRINTING");

        result1.Should().Be(result2);
    }

    [Fact]
    public void NormalizeState_WithWhitespace_TrimsBeforeMapping()
    {
        PrinterStateNormalizer.NormalizeState("  idle  ").Should().Be("Idle");
    }
}
