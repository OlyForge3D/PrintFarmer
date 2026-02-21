#pragma warning disable S1006, CA1033, S1939 // Default parameters, explicit interface implementations, and interface inheritance are intentional

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Contracts.Printers.OctoPrint;

/// <summary>
/// OctoPrint job status DTO - encapsulates parsed /api/job response.
/// </summary>
public sealed class OctoPrintJobStatus
{
    public string? Filename { get; set; }

    public double? Progress { get; set; }

    public double? PrintTime { get; set; }

    public double? PrintTimeLeft { get; set; }

    public Dictionary<string, object>? Filament { get; set; }
}
