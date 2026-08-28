using System;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts;

// Shares ArtifactsMetricsSerialCollection with ArtifactsMetricsTests: this class calls
// ArtifactsMetrics.ResetForTests() per test, which must never interleave with
// ArtifactsMetricsTests' before/after gauge measurements. See the collection definition
// for the full rationale.
[Collection(ArtifactsMetricsSerialCollection.Name)]
public class ArtifactsThresholdTests
{
    [Fact(DisplayName = "Warning threshold event fires when exceeded")]
    public void Warning_Threshold_Event_Fires_When_Exceeded()
    {
        // Arrange
        ArtifactsMetrics.ResetForTests();
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        SlicerStorageThresholdEventArgs? capturedEvent = null;
        metrics.ThresholdExceeded += (sender, e) => capturedEvent = e;

        // Act - Upload enough to exceed warning.
        // ThresholdExceeded fires synchronously inside RecordUpload, so no wait is needed.
        metrics.RecordUpload(1200);

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
        ArtifactsMetrics.ResetForTests();
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        List<SlicerStorageThresholdEventArgs> events = new List<SlicerStorageThresholdEventArgs>();
        metrics.ThresholdExceeded += (sender, e) => events.Add(e);

        // Act - Upload enough to exceed critical.
        // ThresholdExceeded fires synchronously inside RecordUpload, so no wait is needed.
        metrics.RecordUpload(5500);

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
        ArtifactsMetrics.ResetForTests();
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        int eventCount = 0;
        metrics.ThresholdExceeded += (sender, e) => Interlocked.Increment(ref eventCount);

        // Act - Multiple uploads that stay in warning range.
        // ThresholdExceeded fires synchronously inside RecordUpload, so no waits are
        // needed between calls; back-to-back calls also avoid widening the window in
        // which a concurrently-starting test host could mutate the shared static
        // threshold state via ConfigureSlicerMetrics.
        metrics.RecordUpload(800);
        metrics.RecordUpload(300); // Total: 1100, crosses warning
        metrics.RecordUpload(500); // Total: 1600, still warning

        // Assert - Only one event should fire (when first crossing warning)
        _ = eventCount.Should().Be(1);
    }

    [Fact(DisplayName = "Threshold state gauge reflects current state")]
    public void Threshold_State_Gauge_Reflects_Current_State()
    {
        // Arrange
        ArtifactsMetrics.ResetForTests();
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        metrics.SetThresholds(warningBytes: 1000, criticalBytes: 5000);

        using MeterListener meterListener = new MeterListener();
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

        // Upload to warning level. RecordUpload updates the observable state synchronously.
        metrics.RecordUpload(1500);
        meterListener.RecordObservableInstruments();
        int warningState = stateValues.LastOrDefault();

        // Upload to critical level
        metrics.RecordUpload(4000);
        meterListener.RecordObservableInstruments();
        int criticalState = stateValues.LastOrDefault();

        // Assert
        _ = initialState.Should().Be(0); // Normal
        _ = warningState.Should().Be(1); // Warning
        _ = criticalState.Should().Be(2); // Critical
    }

    [Fact(DisplayName = "No events when thresholds not configured")]
    public void No_Events_When_Thresholds_Not_Configured()
    {
        // Arrange
        ArtifactsMetrics.ResetForTests();
        using ArtifactsMetrics metrics = new ArtifactsMetrics();
        // Don't call SetThresholds

        int eventCount = 0;
        metrics.ThresholdExceeded += (sender, e) => Interlocked.Increment(ref eventCount);

        // Act
        metrics.RecordUpload(10000);

        // Assert
        _ = eventCount.Should().Be(0);
    }
}
