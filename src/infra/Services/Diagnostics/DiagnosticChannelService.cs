using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Diagnostics;

/// <summary>
/// Named diagnostic channels that can be toggled at runtime via API.
/// When a channel is enabled, verbose (Information/Debug) logging is emitted
/// for that area. When disabled, only Warning+ flows through.
/// </summary>
public interface IDiagnosticChannelService
{
    /// <summary>
    /// Check whether a named diagnostic channel is currently enabled.
    /// </summary>
    bool IsEnabled(string channel);

    /// <summary>
    /// Enable a diagnostic channel, optionally with an auto-expiry duration.
    /// </summary>
    void Enable(string channel, TimeSpan? autoDisableAfter = null);

    /// <summary>
    /// Disable a diagnostic channel.
    /// </summary>
    void Disable(string channel);

    /// <summary>
    /// Get the current state of all known channels.
    /// </summary>
    IReadOnlyList<DiagnosticChannelState> GetAllChannels();
}

/// <summary>
/// Snapshot of a diagnostic channel's current state.
/// </summary>
public record DiagnosticChannelState(
    string Name,
    string Description,
    bool IsEnabled,
    DateTime? EnabledAt,
    DateTime? ExpiresAt);

/// <summary>
/// Well-known diagnostic channel names.
/// </summary>
public static class DiagnosticChannels
{
    public const string PrinterStateTransitions = "printer-state-transitions";
    public const string OrphanedJobSync = "orphaned-job-sync";
    public const string PrintJobDispatch = "print-job-dispatch";
    public const string BackendPolling = "backend-polling";
    public const string SignalRBroadcast = "signalr-broadcast";
    public const string AutoDispatch = "auto-dispatch";
    public const string FileUpload = "file-upload";
}

public class DiagnosticChannelService : IDiagnosticChannelService
{
    private readonly ConcurrentDictionary<string, ChannelEntry> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DiagnosticChannelService> _logger;

    public DiagnosticChannelService(ILogger<DiagnosticChannelService> logger)
    {
        _logger = logger;

        Register(DiagnosticChannels.PrinterStateTransitions, "Printer state changes in the status cache (idle→printing, online/offline transitions)");
        Register(DiagnosticChannels.OrphanedJobSync, "Orphaned job reconciliation — age checks, printer state lookups, sync decisions");
        Register(DiagnosticChannels.PrintJobDispatch, "Job dispatch lifecycle — Starting, file upload, Printing transition");
        Register(DiagnosticChannels.BackendPolling, "Backend polling cycles (Moonraker, PrusaLink, OctoPrint, SDCP)");
        Register(DiagnosticChannels.SignalRBroadcast, "SignalR hub broadcasts for printer updates and job queue changes");
        Register(DiagnosticChannels.AutoDispatch, "Auto-dispatch decisions — printer selection, queue evaluation");
        Register(DiagnosticChannels.FileUpload, "G-code file upload progress and transfer details");
    }

    public bool IsEnabled(string channel)
    {
        if (!_channels.TryGetValue(channel, out ChannelEntry? entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc.HasValue && DateTime.UtcNow >= entry.ExpiresAtUtc.Value)
        {
            entry.IsEnabled = false;
            entry.EnabledAtUtc = null;
            entry.ExpiresAtUtc = null;
            return false;
        }

        return entry.IsEnabled;
    }

    public void Enable(string channel, TimeSpan? autoDisableAfter = null)
    {
        if (!_channels.TryGetValue(channel, out ChannelEntry? entry))
        {
            _logger.LogWarning("[Diagnostics] Unknown channel '{Channel}' — ignoring enable request", channel);
            return;
        }

        entry.IsEnabled = true;
        entry.EnabledAtUtc = DateTime.UtcNow;
        entry.ExpiresAtUtc = autoDisableAfter.HasValue ? DateTime.UtcNow + autoDisableAfter.Value : null;

        _logger.LogWarning(
            "[Diagnostics] Channel '{Channel}' ENABLED{Expiry}",
            channel,
            entry.ExpiresAtUtc.HasValue ? $" (auto-disables at {entry.ExpiresAtUtc.Value:HH:mm:ss} UTC)" : " (no expiry)");
    }

    public void Disable(string channel)
    {
        if (!_channels.TryGetValue(channel, out ChannelEntry? entry))
        {
            return;
        }

        entry.IsEnabled = false;
        entry.EnabledAtUtc = null;
        entry.ExpiresAtUtc = null;

        _logger.LogWarning("[Diagnostics] Channel '{Channel}' DISABLED", channel);
    }

    public IReadOnlyList<DiagnosticChannelState> GetAllChannels()
    {
        return _channels.Values
            .Select(e => new DiagnosticChannelState(
                e.Name,
                e.Description,
                IsEnabled(e.Name),
                e.EnabledAtUtc,
                e.ExpiresAtUtc))
            .OrderBy(c => c.Name)
            .ToList();
    }

    private void Register(string name, string description)
    {
        _channels[name] = new ChannelEntry(name, description);
    }

    private sealed class ChannelEntry(string name, string description)
    {
        public string Name { get; } = name;

        public string Description { get; } = description;

        public bool IsEnabled { get; set; }

        public DateTime? EnabledAtUtc { get; set; }

        public DateTime? ExpiresAtUtc { get; set; }
    }
}
