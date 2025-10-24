using System;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Artifacts;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Artifacts;

public class ArtifactsThresholdTests
{
    [Fact(DisplayName = "Warning threshold event fires when exceeded")]
    public void Warning_Threshold_Event_Fires_When_Exceeded()
    {
        // Arrange
        using var metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        StorageThresholdEventArgs? capturedEvent = null;
        metrics.ThresholdExceeded += (sender, e) => capturedEvent = e;

        // Act - Upload enough to exceed warning
        metrics.RecordUpload(1200);

        // Give event time to fire
        Thread.Sleep(50);

        // Assert
        capturedEvent.Should().NotBeNull();
        capturedEvent!.Level.Should().Be(StorageThresholdLevel.Warning);
        capturedEvent.CurrentBytes.Should().Be(1200);
        capturedEvent.WarningThreshold.Should().Be(1000);
    }

    [Fact(DisplayName = "Critical threshold event fires when exceeded")]
    public void Critical_Threshold_Event_Fires_When_Exceeded()
    {
        // Arrange
        using var metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        var events = new System.Collections.Generic.List<StorageThresholdEventArgs>();
        metrics.ThresholdExceeded += (sender, e) => events.Add(e);

        // Act - Upload enough to exceed critical
        metrics.RecordUpload(5500);

        // Give event time to fire
        Thread.Sleep(50);

        // Assert
        events.Should().ContainSingle(e => e.Level == StorageThresholdLevel.Critical);
        var criticalEvent = events.First(e => e.Level == StorageThresholdLevel.Critical);
        criticalEvent.CurrentBytes.Should().Be(5500);
        criticalEvent.CriticalThreshold.Should().Be(5000);
    }

    [Fact(DisplayName = "Multiple uploads trigger warning only once")]
    public void Multiple_Uploads_Trigger_Warning_Only_Once()
    {
        // Arrange
        using var metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        int eventCount = 0;
        metrics.ThresholdExceeded += (sender, e) => Interlocked.Increment(ref eventCount);

        // Act - Multiple uploads that stay in warning range
        metrics.RecordUpload(800);
        Thread.Sleep(20);
        metrics.RecordUpload(300); // Total: 1100, crosses warning
        Thread.Sleep(20);
        metrics.RecordUpload(500); // Total: 1600, still warning
        Thread.Sleep(20);

        // Assert - Only one event should fire (when first crossing warning)
        eventCount.Should().Be(1);
    }

    [Fact(DisplayName = "Threshold state gauge reflects current state")]
    public void Threshold_State_Gauge_Reflects_Current_State()
    {
        // Arrange
        using var metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);
        // Act & Assert using instance-local state (avoids global MeterListener interference)
        var initialState = metrics.CurrentState;

        // Upload to warning level
        metrics.RecordUpload(1500);
        Thread.Sleep(50);
        var warningState = metrics.CurrentState;

        // Upload to critical level
        metrics.RecordUpload(4000);
        Thread.Sleep(50);
        var criticalState = metrics.CurrentState;

        // Assert
        initialState.Should().Be(0); // Normal
        warningState.Should().Be(1); // Warning
        criticalState.Should().Be(2); // Critical
    }

    [Fact(DisplayName = "No events when thresholds not configured")]
    public void No_Events_When_Thresholds_Not_Configured()
    {
        // Arrange
        using var metrics = new ArtifactsMetrics();
        // Don't call SetThresholds

        int eventCount = 0;
        metrics.ThresholdExceeded += (sender, e) => Interlocked.Increment(ref eventCount);

        // Act
        metrics.RecordUpload(10000);
        Thread.Sleep(50);

        // Assert
        eventCount.Should().Be(0);
    }
}
