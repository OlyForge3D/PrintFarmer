using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

/// <summary>
/// Tests for the per-tool attribution capability derivation (issue #711, round-14). The capability
/// flag is genuine, not cosmetic: it is only true for a Moonraker printer with two or more physical
/// hotends — the single case where interval-aware active-tool telemetry can differentiate per-head
/// wear. Every other backend stays false so no per-toolhead wear is ever fabricated.
/// </summary>
public class PrintersServicePerToolAttributionFlagTests
{
    [Fact]
    public void DeterminePerToolAttributionSupport_MoonrakerWithTwoPhysicalHotends_ReturnsTrue()
    {
        List<Toolhead> toolheads =
        [
            Physical(0),
            Physical(1)
        ];

        PerToolAttributionCapability.IsSupported(PrinterBackend.Moonraker, toolheads)
            .Should().BeTrue();
    }

    [Fact]
    public void DeterminePerToolAttributionSupport_MoonrakerWithSingleHotend_ReturnsFalse()
    {
        // A single-hotend printer, even with an MMU/AMS, has just one wear-bearing head — there is
        // nothing to differentiate, so the flag stays false.
        List<Toolhead> toolheads =
        [
            Physical(0),
            MmuGate(0),
            MmuGate(1),
            MmuGate(2),
            MmuGate(3)
        ];

        PerToolAttributionCapability.IsSupported(PrinterBackend.Moonraker, toolheads)
            .Should().BeFalse();
    }

    [Fact]
    public void DeterminePerToolAttributionSupport_CountsOnlyPhysicalHotends()
    {
        // Two physical hotends plus MMU gates → true; the virtual gates do not count toward the
        // physical hotend total.
        List<Toolhead> toolheads =
        [
            Physical(0),
            Physical(1),
            MmuGate(0),
            MmuGate(1)
        ];

        PerToolAttributionCapability.IsSupported(PrinterBackend.Moonraker, toolheads)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(PrinterBackend.PrusaLink)]
    [InlineData(PrinterBackend.SDCP)]
    [InlineData(PrinterBackend.OctoPrint)]
    [InlineData(PrinterBackend.Unknown)]
    public void DeterminePerToolAttributionSupport_NonMoonrakerBackends_ReturnFalseEvenWithMultiplePhysical(
        PrinterBackend backend)
    {
        // Regression guard (issue #711, round-10 Finding 1): a backend without interval-aware
        // active-tool telemetry must never claim per-tool attribution, so it can never fabricate
        // per-head wear, regardless of how many physical hotends it reports.
        List<Toolhead> toolheads =
        [
            Physical(0),
            Physical(1)
        ];

        PerToolAttributionCapability.IsSupported(backend, toolheads)
            .Should().BeFalse();
    }

    [Fact]
    public void DeterminePerToolAttributionSupport_MoonrakerWithNoToolheads_ReturnsFalse()
    {
        PerToolAttributionCapability.IsSupported(PrinterBackend.Moonraker, [])
            .Should().BeFalse();
    }

    [Fact]
    public void Refresh_NewMoonrakerWithTwoPhysicalToolheads_SetsCapabilityTrue()
    {
        Printer printer = PrinterWithBackend(PrinterBackend.Moonraker);
        printer.Toolheads.Add(Physical(0));
        printer.Toolheads.Add(Physical(1));

        bool changed = PerToolAttributionCapability.Refresh(printer);

        changed.Should().BeTrue();
        printer.SupportsPerToolAttribution.Should().BeTrue();
    }

    [Fact]
    public void Refresh_BackendChangedFromMoonrakerToPrusaLink_FlipsCapabilityFalse()
    {
        Printer printer = PrinterWithBackend(PrinterBackend.Moonraker);
        printer.Toolheads.Add(Physical(0));
        printer.Toolheads.Add(Physical(1));
        _ = PerToolAttributionCapability.Refresh(printer);
        printer.Backend = (int)PrinterBackend.PrusaLink;

        bool changed = PerToolAttributionCapability.Refresh(printer);

        changed.Should().BeTrue();
        printer.SupportsPerToolAttribution.Should().BeFalse();
    }

    [Fact]
    public void Refresh_DerivedValueUnchanged_PreservesEqualityGuard()
    {
        Printer printer = PrinterWithBackend(PrinterBackend.PrusaLink);

        PerToolAttributionCapability.Refresh(printer).Should().BeFalse();
        printer.SupportsPerToolAttribution.Should().BeFalse();
    }

    private static Printer PrinterWithBackend(PrinterBackend backend) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Capability test",
        Backend = (int)backend
    };

    private static Toolhead Physical(int index) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Hotend {index}",
        Index = index,
        ToolheadType = ToolheadType.Physical
    };

    private static Toolhead MmuGate(int index) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Gate {index}",
        Index = index,
        ToolheadType = ToolheadType.MmuGate
    };
}
