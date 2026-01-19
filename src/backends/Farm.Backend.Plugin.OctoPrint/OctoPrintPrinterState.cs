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

namespace Farm.Backend.Plugin.OctoPrint;

#pragma warning disable CS1066 // Default value for optional parameter not enforced for interface members

/// <summary>
/// OctoPrint printer state DTO - encapsulates parsed /api/printer response.
/// </summary>
public sealed class OctoPrintPrinterState
{
    public bool Operational { get; set; }

    public bool Printing { get; set; }

    public string State { get; set; } = string.Empty;

    public Dictionary<string, object>? Temperatures { get; set; }
}

#pragma warning restore CS1066
