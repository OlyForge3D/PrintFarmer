#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;

public class SimplePrinterStatus
{
    public string State { get; set; } = string.Empty;

    public bool IsOnline { get; set; }

    public double? NozzleTemp { get; set; }

    public double? NozzleTarget { get; set; }

    public double? BedTemp { get; set; }

    public double? BedTarget { get; set; }

    public double? AxisX { get; set; }

    public double? AxisY { get; set; }

    public double? AxisZ { get; set; }

    public int? FanSpeed { get; set; }

    public int? FlowRate { get; set; }

    public int? SpeedMultiplier { get; set; }

    public bool IsPrinting => State == PrinterStates.Printing;

    public bool IsPaused => State == PrinterStates.Paused;

    public bool IsIdle => State == PrinterStates.Idle;

    public bool HasError => State == PrinterStates.Error;

    public bool NeedsAttention => State == PrinterStates.Attention;
}

#pragma warning restore CS1066
