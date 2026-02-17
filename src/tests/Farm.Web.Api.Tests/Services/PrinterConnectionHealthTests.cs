using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class PrinterConnectionHealthTests
{
    [Fact]
    public void RecordTransition_SameState_NoTransitionRecorded()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        health.RecordTransition(PrinterConnectionState.Connected, "duplicate");

        Assert.Empty(health.RecentTransitions);
    }

    [Fact]
    public void RecordTransition_OnlineToReconnecting_RecordsTransition()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        health.RecordTransition(PrinterConnectionState.Reconnecting, "Klippy disconnected");

        Assert.Single(health.RecentTransitions);
        Assert.Equal(PrinterConnectionState.Connected, health.RecentTransitions[0].FromState);
        Assert.Equal(PrinterConnectionState.Reconnecting, health.RecentTransitions[0].ToState);
        Assert.Equal("Klippy disconnected", health.RecentTransitions[0].Reason);
        Assert.Equal(PrinterConnectionState.Reconnecting, health.ConnectionState);
    }

    [Fact]
    public void RecordTransition_OfflineToConnected_IncrementsTotalReconnects()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Offline
        };

        health.RecordTransition(PrinterConnectionState.Connected, "Reconnected");

        Assert.Equal(1, health.TotalReconnects);
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.NotNull(health.LastConnectedUtc);
    }

    [Fact]
    public void RecordTransition_ConnectedToOffline_SetsLastDisconnectedUtc()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        health.RecordTransition(PrinterConnectionState.Offline, "Grace expired");

        Assert.NotNull(health.LastDisconnectedUtc);
        Assert.Equal(PrinterConnectionState.Offline, health.ConnectionState);
    }

    [Fact]
    public void RecordTransition_RingBuffer_CapsAtMaxTransitions()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        // Record more than MaxTransitions alternating transitions
        for (int i = 0; i < PrinterConnectionHealth.MaxTransitions + 5; i++)
        {
            var next = i % 2 == 0 ? PrinterConnectionState.Reconnecting : PrinterConnectionState.Connected;
            health.RecordTransition(next, $"transition {i}");
        }

        Assert.Equal(PrinterConnectionHealth.MaxTransitions, health.RecentTransitions.Count);
    }

    [Fact]
    public void UpdateUptimePercent_AlwaysConnected_Returns100()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        health.UpdateUptimePercent(TimeSpan.FromHours(1));

        Assert.Equal(100.0, health.UptimePercent);
    }

    [Fact]
    public void UpdateUptimePercent_AlwaysOffline_Returns0()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Offline
        };

        health.UpdateUptimePercent(TimeSpan.FromHours(1));

        Assert.Equal(0.0, health.UptimePercent);
    }

    [Fact]
    public void RecordTransition_MultipleReconnects_CountsCorrectly()
    {
        var health = new PrinterConnectionHealth
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "TestPrinter",
            Backend = PrinterBackend.Moonraker,
            ConnectionState = PrinterConnectionState.Connected
        };

        // Simulate 3 disconnect/reconnect cycles
        health.RecordTransition(PrinterConnectionState.Offline, "lost");
        health.RecordTransition(PrinterConnectionState.Connected, "recovered");
        health.RecordTransition(PrinterConnectionState.Reconnecting, "hiccup");
        health.RecordTransition(PrinterConnectionState.Connected, "recovered");
        health.RecordTransition(PrinterConnectionState.Offline, "lost again");
        health.RecordTransition(PrinterConnectionState.Connected, "recovered again");

        Assert.Equal(3, health.TotalReconnects);
        Assert.Equal(6, health.RecentTransitions.Count);
    }
}
