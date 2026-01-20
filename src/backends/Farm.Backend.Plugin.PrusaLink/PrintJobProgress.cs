#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;
#pragma warning restore CA1056

// Simplified models and exception previously in extensions
public class PrintJobProgress
{
    public int JobId { get; set; }

    public string State { get; set; } = string.Empty;

    public double Progress { get; set; }

    public int TimePrinting { get; set; }

    public int? TimeRemaining { get; set; }

    public string? FileName { get; set; }

    public bool InaccurateEstimates { get; set; }

    public bool IsActive => State is JobStates.Printing or JobStates.Paused;

    public bool IsFinished => State is JobStates.Finished or JobStates.Stopped;

    public bool HasError => State == JobStates.Error;

    public TimeSpan PrintingTime => TimeSpan.FromSeconds(TimePrinting);

    public TimeSpan? RemainingTime => TimeRemaining.HasValue ? TimeSpan.FromSeconds(TimeRemaining.Value) : null;
}

#pragma warning restore CS1066
