using System;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts;

public class ArtifactsThresholdTests
{
    [Fact(DisplayName = "Warning threshold event fires when exceeded")]
    public void Warning_Threshold_Event_Fires_When_Exceeded()
    {
        // Arrange
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        SlicerStorageThresholdEventArgs? capturedEvent = null;
        metrics.ThresholdExceeded += (sender, e) => capturedEvent = e;

        // Act - Upload enough to exceed warning
        metrics.RecordUpload(1200);

        // Give event time to fire
        Thread.Sleep(50);

        // Assert
        _ = capturedEvent.Should().NotBeNull();
        _ = capturedEvent!.Level.Should().Be(SlicerStorageThresholdLevel.Warning);
        _ = capturedEvent.CurrentBytes.Should().Be(1200);
        _ = capturedEvent.WarningThreshold.Should().Be(1000);
    }

    [Fact(DisplayName = "Critical threshold event fires when exceeded")]
    public void Critical_Threshold_Event_Fires_When_Exceeded()
    {
        // Arrange
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        List<SlicerStorageThresholdEventArgs> events = new List<SlicerStorageThresholdEventArgs>();
        metrics.ThresholdExceeded += (sender, e) => events.Add(e);

        // Act - Upload enough to exceed critical
        metrics.RecordUpload(5500);

        // Give event time to fire
        Thread.Sleep(50);

        // Assert
        _ = events.Should().ContainSingle(e => e.Level == SlicerStorageThresholdLevel.Critical);
        SlicerStorageThresholdEventArgs criticalEvent = events.First(e => e.Level == SlicerStorageThresholdLevel.Critical);
        _ = criticalEvent.CurrentBytes.Should().Be(5500);
        _ = criticalEvent.CriticalThreshold.Should().Be(5000);
    }

    [Fact(DisplayName = "Multiple uploads trigger warning only once")]
    public void Multiple_Uploads_Trigger_Warning_Only_Once()
    {
        // Arrange
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
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
        _ = eventCount.Should().Be(1);
    }

    [Fact(DisplayName = "Threshold state gauge reflects current state")]
    public void Threshold_State_Gauge_Reflects_Current_State()
    {
        // Arrange
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        MeterListener meterListener = new MeterListener();
        List<int> stateValues = new List<int>();

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "PrintFarmer.Artifacts" &&
                instrument.Name == "printfarmer.artifacts.storage_threshold_state")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "printfarmer.artifacts.storage_threshold_state")
            {
                stateValues.Add(measurement);
            }
        });

        meterListener.Start();

        // Act - Record initial state (normal)
        meterListener.RecordObservableInstruments();
        int initialState = stateValues.LastOrDefault();

        // Upload to warning level
        metrics.RecordUpload(1500);
        Thread.Sleep(50);
        meterListener.RecordObservableInstruments();
        int warningState = stateValues.LastOrDefault();

        // Upload to critical level
        metrics.RecordUpload(4000);
        Thread.Sleep(50);
        meterListener.RecordObservableInstruments();
        int criticalState = stateValues.LastOrDefault();

        meterListener.Dispose();

        // Assert
        _ = initialState.Should().Be(0); // Normal
        _ = warningState.Should().Be(1); // Warning
        _ = criticalState.Should().Be(2); // Critical
    }

    [Fact(DisplayName = "No events when thresholds not configured")]
    public void No_Events_When_Thresholds_Not_Configured()
    {
        // Arrange
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        // Don't call SetThresholds

        int eventCount = 0;
        metrics.ThresholdExceeded += (sender, e) => Interlocked.Increment(ref eventCount);

        // Act
        metrics.RecordUpload(10000);
        Thread.Sleep(50);

        // Assert
        _ = eventCount.Should().Be(0);
    }
}
