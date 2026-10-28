using System.Diagnostics.Metrics;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Tests.Services;

public sealed partial class PrinterFileEnrichmentAcquisitionTests
{
    [Theory]
    [InlineData(PrinterBackend.Moonraker, "moonraker")]
    [InlineData(PrinterBackend.OctoPrint, "octoprint")]
    [InlineData(PrinterBackend.PrusaLink, "prusalink")]
    [InlineData(PrinterBackend.SDCP, "unknown")]
    [InlineData((PrinterBackend)999, "unknown")]
    public async Task AcquireAsync_Telemetry_CountsNativeOnlyAcquisitionsWithoutFallback(
        PrinterBackend backend, string label)
    {
        using MetricsCapture capture = new();
        FakeFile file = new(Gcode("G28"));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            PrinterFileEnrichmentContent result = await MeasuredAsync(
                capture, file.Length, Sources(file, Native(CompleteNative(), Image(Png))), backend: backend);
            Assert.Equal(PrinterFileEnrichmentState.Ready, result.ThumbnailState);
        }

        Assert.Empty(file.Ranges);
        Assert.Equal(2, capture.Samples.Count);
        Assert.All(capture.Samples, sample =>
        {
            Assert.Equal("printer_file.enrichment.started", sample.Name);
            Assert.Equal(1, sample.Value);
            Assert.Equal(new KeyValuePair<string, object?>("backend", label), Assert.Single(sample.Tags));
        });
    }

    [Theory]
    [InlineData(false, true, true, "metadata")]
    [InlineData(false, false, true, "metadata_and_thumbnail")]
    [InlineData(true, false, true, "thumbnail")]
    [InlineData(false, false, false, "metadata")]
    public async Task AcquireAsync_Telemetry_RecordsOneFallbackForItsTriggerAndExcludesNativeAndVerificationTime(
        bool completeMetadata, bool nativeImage, bool captureThumbnail, string trigger)
    {
        using MetricsCapture capture = new();
        byte[] content = Gcode("; layer_height = 0.3", ThumbnailBlock);
        PrinterFileEnrichmentSources sources = new(
            _ =>
            {
                capture.Clock.Advance(2000);
                return Task.FromResult<PrinterFileNativeEnrichment?>(new(
                    completeMetadata ? CompleteNative() : PartialNative(), null, _ =>
                    {
                        capture.Clock.Advance(3000);
                        return Task.FromResult<byte[]?>(nativeImage ? Png : null);
                    }));
            },
            (offset, _, _, budget) =>
            {
                budget.BeginRequest();
                capture.Clock.Advance(250);
                return Task.FromResult(new PrinterFileRegion(content, offset, true));
            },
            _ =>
            {
                capture.Clock.Advance(4000);
                return Task.CompletedTask;
            });

        PrinterFileEnrichmentContent result = await MeasuredAsync(
            capture, content.Length, sources, captureThumbnail);

        Assert.Equal(PrinterFileEnrichmentState.Ready, result.MetadataState);
        Assert.Equal(captureThumbnail ? PrinterFileEnrichmentState.Ready : PrinterFileEnrichmentState.Deferred,
            result.ThumbnailState);
        capture.AssertFallback(trigger, "completed", 250);
    }

    [Fact]
    public async Task AcquireAsync_Telemetry_HeadAndTailAreOneFallbackWithTheirCombinedElapsedTime()
    {
        using MetricsCapture capture = new();
        List<long> offsets = [];
        PrinterFileEnrichmentSources sources = new(null, (offset, _, _, budget) =>
        {
            budget.BeginRequest();
            offsets.Add(offset);
            capture.Clock.Advance(offset == 0 ? 150 : 350);
            return Task.FromResult(new PrinterFileRegion(Gcode("; layer_height = 0.3"), offset, false));
        });

        PrinterFileEnrichmentContent result = await MeasuredAsync(capture, ThreeMiB, sources);

        Assert.Equal([0L, ThreeMiB - OneMiB], offsets);
        Assert.Equal(0.3, result.Metadata?.LayerHeightMm);
        capture.AssertFallback("metadata_and_thumbnail", "completed", 500);
    }

    [Theory]
    [InlineData("deadline", "timeout")]
    [InlineData("transfer_failed", "failed")]
    [InlineData("source_changed", "failed")]
    [InlineData("authentication_required", "failed")]
    [InlineData("private-path-and-credential-must-not-be-a-label", "failed")]
    public async Task AcquireAsync_Telemetry_FailuresAreTimedWithoutExportingRawReasons(string reason, string outcome)
    {
        using MetricsCapture capture = new();
        PrinterFileEnrichmentSources sources = new(null, (_, _, _, _) =>
        {
            capture.Clock.Advance(750);
            throw new PrinterFileCopyException(reason);
        });

        PrinterFileEnrichmentContent result = await MeasuredAsync(capture, 100, sources);

        Assert.Equal(reason, result.MetadataReason);
        capture.AssertFallback("metadata_and_thumbnail", outcome, 750);
    }

    [Fact]
    public async Task AcquireAsync_Telemetry_RealDeadlineRecordsTimeoutAndPreservesDeferredResult()
    {
        using MetricsCapture capture = new();
        PrinterFileEnrichmentSources sources = new(null, async (_, _, _, budget) =>
        {
            budget.BeginRequest();
            capture.Clock.Advance(15000);
            await Task.Delay(Timeout.InfiniteTimeSpan, budget.Token);
            throw new InvalidOperationException("The deadline must cancel the stalled read.");
        });

        PrinterFileEnrichmentContent result = await MeasuredAsync(capture, 100, sources);

        Assert.Equal(PrinterFileEnrichmentState.Deferred, result.MetadataState);
        Assert.Equal("deadline", result.MetadataReason);
        Assert.Equal(PrinterFileEnrichmentState.Deferred, result.ThumbnailState);
        Assert.Equal("deadline", result.ThumbnailReason);
        capture.AssertFallback("metadata_and_thumbnail", "timeout", 15000);
    }

    [Fact]
    public async Task AcquireAsync_Telemetry_TailFailureKeepsPartialMetadataAndMeasuresBothReads()
    {
        using MetricsCapture capture = new();
        PrinterFileEnrichmentSources sources = new(null, (offset, _, _, _) =>
        {
            capture.Clock.Advance(125);
            if (offset > 0)
            {
                throw new PrinterFileCopyException("transfer_failed");
            }

            return Task.FromResult(new PrinterFileRegion(Gcode("; layer_height = 0.3"), 0, false));
        });

        PrinterFileEnrichmentContent result = await MeasuredAsync(capture, ThreeMiB, sources);

        Assert.Equal(PrinterFileEnrichmentState.Partial, result.MetadataState);
        Assert.Equal(0.3, result.Metadata?.LayerHeightMm);
        capture.AssertFallback("metadata_and_thumbnail", "partial", 250);
    }

    [Fact]
    public async Task AcquireAsync_Telemetry_CallerCancellationIsTimedAndStillPropagates()
    {
        using MetricsCapture capture = new();
        using CancellationTokenSource cancelled = new();
        PrinterFileEnrichmentSources sources = new(null, (_, _, _, budget) =>
        {
            capture.Clock.Advance(40);
            cancelled.Cancel();
            budget.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MeasuredAsync(capture, 100, sources, ct: cancelled.Token));

        capture.AssertFallback("metadata_and_thumbnail", "cancelled", 40);
    }

    [Fact]
    public async Task AcquireAsync_Telemetry_UnexpectedErrorsAreTimedAndNotSwallowed()
    {
        using MetricsCapture capture = new();
        InvalidOperationException error = new("synthetic private file path");
        PrinterFileEnrichmentSources sources = new(null, (_, _, _, _) =>
        {
            capture.Clock.Advance(60);
            throw error;
        });

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MeasuredAsync(capture, 100, sources)));

        capture.AssertFallback("metadata_and_thumbnail", "error", 60);
    }

    [Theory]
    [InlineData("request_limit", false)]
    [InlineData("request_limit", true)]
    [InlineData("response_limit", false)]
    [InlineData("empty_source", false)]
    public async Task AcquireAsync_Telemetry_SkippedFallbackHasNoStartOrDuration(string reason, bool thumbnailOnly)
    {
        using MetricsCapture capture = new();
        FakeFile file = new(Gcode("; layer_height = 0.3", ThumbnailBlock));
        PrinterFileEnrichmentSources sources = Sources(file, budget =>
        {
            if (reason == "request_limit")
            {
                SpendRequests(budget);
            }
            else if (reason == "response_limit")
            {
                budget.RecordRead(PrinterFileEnrichmentLimits.MaximumResponseBytes);
            }

            return Task.FromResult<PrinterFileNativeEnrichment?>(new(thumbnailOnly ? CompleteNative() : null));
        });

        PrinterFileEnrichmentContent result = await MeasuredAsync(capture, reason == "empty_source" ? 0 : file.Length, sources);

        Assert.Empty(file.Ranges);
        Assert.Equal(reason == "empty_source" ? PrinterFileEnrichmentState.Absent : PrinterFileEnrichmentState.Deferred,
            result.ThumbnailState);
        Assert.Equal(2, capture.Samples.Count);
        MetricSample skipped = Assert.Single(capture.Samples, sample => sample.Name.EndsWith(".skipped", StringComparison.Ordinal));
        Assert.Equal(1, skipped.Value);
        Assert.Equal(3, skipped.Tags.Count);
        Assert.Equal("moonraker", skipped.Tags["backend"]);
        Assert.Equal(thumbnailOnly ? "thumbnail" : "metadata_and_thumbnail", skipped.Tags["trigger"]);
        Assert.Equal(reason, skipped.Tags["reason"]);
    }

    [Fact]
    public async Task AcquireAsync_Telemetry_BinaryFallbackIsAlsoMeasured()
    {
        using MetricsCapture capture = new();
        byte[] content = BinaryGcode("layer_height = 0.2\n", Png);
        PrinterFileEnrichmentSources sources = new(null, (_, _, _, _) =>
        {
            capture.Clock.Advance(80);
            return Task.FromResult(new PrinterFileRegion(content, 0, true));
        });

        PrinterFileEnrichmentContent result = await MeasuredAsync(capture, content.Length, sources);

        Assert.Equal("bounded_bgcode", result.MetadataSource);
        Assert.Equal(Png, result.Image);
        capture.AssertFallback("metadata_and_thumbnail", "completed", 80);
    }

    private static Task<PrinterFileEnrichmentContent> MeasuredAsync(
        MetricsCapture capture, long size, PrinterFileEnrichmentSources sources, bool captureThumbnail = true,
        PrinterBackend backend = PrinterBackend.Moonraker, CancellationToken ct = default) =>
        PrinterFileEnrichmentAcquisition.AcquireAsync(
            new("private-storage", "private-folder/private-file.gcode", size, ModifiedValue: "private-revision"),
            sources, captureThumbnail, ct, backend: backend, metrics: capture.Metrics);

    private sealed class MetricsCapture : IDisposable
    {
        private readonly MeterListener listener = new();

        public MetricsCapture()
        {
            Metrics = new(Clock);
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter == Metrics.Meter)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.Start();
        }

        public ManualMetricClock Clock { get; } = new();

        public PrinterFileEnrichmentMetrics Metrics { get; }

        public List<MetricSample> Samples { get; } = [];

        public void AssertFallback(string trigger, string outcome, double milliseconds)
        {
            Assert.Equal(3, Samples.Count);
            MetricSample enrichment = Assert.Single(Samples, sample => sample.Name == "printer_file.enrichment.started");
            Assert.Equal(1, enrichment.Value);
            Assert.Equal(new KeyValuePair<string, object?>("backend", "moonraker"), Assert.Single(enrichment.Tags));
            MetricSample start = Assert.Single(Samples, sample => sample.Name == "printer_file.enrichment.fallback.started");
            Assert.Equal(1, start.Value);
            Assert.Equal(2, start.Tags.Count);
            Assert.Equal("moonraker", start.Tags["backend"]);
            Assert.Equal(trigger, start.Tags["trigger"]);
            MetricSample duration = Assert.Single(Samples, sample => sample.Name == PrinterFileEnrichmentMetrics.FallbackDurationName);
            Assert.Equal(milliseconds, duration.Value);
            Assert.Equal("ms", duration.Unit);
            Assert.Equal(3, duration.Tags.Count);
            Assert.Equal("moonraker", duration.Tags["backend"]);
            Assert.Equal(trigger, duration.Tags["trigger"]);
            Assert.Equal(outcome, duration.Tags["outcome"]);
        }

        public void Dispose()
        {
            listener.Dispose();
            Metrics.Dispose();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            Samples.Add(new(instrument.Name, instrument.Unit, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
    }

    private sealed class ManualMetricClock : TimeProvider
    {
        private long milliseconds;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => milliseconds;

        public void Advance(long elapsed) => milliseconds += elapsed;
    }

    private sealed record MetricSample(string Name, string? Unit, double Value, Dictionary<string, object?> Tags);
}
