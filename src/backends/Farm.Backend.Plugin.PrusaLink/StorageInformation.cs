#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;

public class StorageInformation
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool Available { get; set; }

    public bool ReadOnly { get; set; }

    public long? FreeSpace { get; set; }

    public long? TotalSpace { get; set; }

    public long? PrintFileSize { get; set; }

    public long? SystemFileSize { get; set; }

    public double? UsagePercentage => TotalSpace.HasValue && TotalSpace > 0
        ? (double)(TotalSpace.Value - (FreeSpace ?? 0)) / TotalSpace.Value * 100
        : null;
}

#pragma warning restore CS1066
