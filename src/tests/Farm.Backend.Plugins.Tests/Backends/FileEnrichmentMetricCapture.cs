using System.Diagnostics;
using System.Diagnostics.Metrics;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugins.Tests.Backends;

internal sealed class FileEnrichmentMetricCapture : IDisposable
{
    private readonly Activity activity = new Activity("enrichment-metric-test").Start();
    private readonly MeterListener listener = new();
    private readonly List<Sample> samples = [];

    public FileEnrichmentMetricCapture()
    {
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PrinterFileEnrichmentMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
    }

    public void AssertAcquisition(string backend, bool fallback)
    {
        Assert.Equal(fallback ? 3 : 1, samples.Count);
        Sample enrichment = Assert.Single(samples, sample => sample.Name == "printer_file.enrichment.started");
        Assert.Equal(1, enrichment.Value);
        Assert.Equal(new KeyValuePair<string, object?>("backend", backend), Assert.Single(enrichment.Tags));
        if (!fallback)
        {
            return;
        }

        Sample start = Assert.Single(samples, sample => sample.Name == "printer_file.enrichment.fallback.started");
        Assert.Equal(1, start.Value);
        Assert.Equal(2, start.Tags.Count);
        Assert.Equal(backend, start.Tags["backend"]);
        Assert.Equal("metadata", start.Tags["trigger"]);
        Sample duration = Assert.Single(samples, sample => sample.Name == PrinterFileEnrichmentMetrics.FallbackDurationName);
        Assert.True(duration.Value >= 0);
        Assert.Equal(3, duration.Tags.Count);
        Assert.Equal(backend, duration.Tags["backend"]);
        Assert.Equal("metadata", duration.Tags["trigger"]);
        Assert.Equal("completed", duration.Tags["outcome"]);
    }

    public void Dispose()
    {
        listener.Dispose();
        activity.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // The production meter is shared; only capture this test's async execution context.
        if (Activity.Current == activity)
        {
            samples.Add(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        }
    }

    private sealed record Sample(string Name, double Value, Dictionary<string, object?> Tags);
}
