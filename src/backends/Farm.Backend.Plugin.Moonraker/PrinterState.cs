using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.Moonraker;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Backend.Plugin.Moonraker;

// Persistent state for a printer to avoid overwriting good values with nulls
internal sealed class PrinterState
{
    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    public double? HotendTemp { get; set; }

    public double? BedTemp { get; set; }

    public double? HotendTarget { get; set; }

    public double? BedTarget { get; set; }

    public string? State { get; set; }

    public double? Progress { get; set; }

    public string? JobName { get; set; }

    public string? HomedAxes { get; set; }

    public string? CameraStreamUrl { get; set; }

    public string? ThumbnailUrl { get; set; }
}
