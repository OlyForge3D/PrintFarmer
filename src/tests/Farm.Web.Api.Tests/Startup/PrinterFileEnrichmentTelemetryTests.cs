using System.Globalization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace Farm.Web.Api.Tests.Startup;

[CollectionDefinition("Printer file telemetry export", DisableParallelization = true)]
public sealed class PrinterFileTelemetryExportCollection;

[Collection("Printer file telemetry export")]
public sealed class PrinterFileEnrichmentTelemetryTests
{
    [Fact]
    public async Task ProductionTelemetry_ExportsFallbackCountersLabelsAndDeadlineScaleHistogram()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "TelemetryTests",
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddPrintFarmerTelemetry(builder.Configuration, builder.Environment);
        await using WebApplication app = builder.Build();
        app.MapPrometheusScrapingEndpoint();
        await app.StartAsync();
        ManualClock clock = new();
        using PrinterFileEnrichmentMetrics metrics = new(clock);

        await AcquireAsync(metrics, clock, 250);
        await AcquireAsync(metrics, clock, 16000);
        await AcquireAsync(metrics, clock, 15000, timeout: true);

        using HttpClient client = app.GetTestClient();
        string scrape = await client.GetStringAsync("/metrics");
        Assert.False(string.IsNullOrWhiteSpace(scrape), "The production Prometheus endpoint returned no metrics.");
        string[] lines = scrape.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        const string histogram = "printer_file_enrichment_fallback_duration_milliseconds";

        AssertSeries(lines, "printer_file_enrichment_started_total", 3);
        AssertSeries(lines, "printer_file_enrichment_fallback_started_total", 3, "trigger=\"metadata\"");
        AssertSeries(lines, histogram + "_count", 2, "outcome=\"completed\"");
        AssertSeries(lines, histogram + "_sum", 16250, "outcome=\"completed\"");
        AssertSeries(lines, histogram + "_count", 1, "outcome=\"timeout\"");
        AssertSeries(lines, histogram + "_sum", 15000, "outcome=\"timeout\"");
        AssertSeries(lines, histogram + "_bucket", 1, "outcome=\"completed\"", "le=\"15000\"");
        AssertSeries(lines, histogram + "_bucket", 2, "outcome=\"completed\"", "le=\"30000\"");
        AssertSeries(lines, histogram + "_bucket", 2, "outcome=\"completed\"", "le=\"+Inf\"");
        AssertSeries(lines, histogram + "_bucket", 1, "outcome=\"timeout\"", "le=\"15000\"");
        Assert.DoesNotContain("private-file", scrape, StringComparison.Ordinal);
        Assert.DoesNotContain("private-storage", scrape, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Testing", false)]
    [InlineData("TelemetryTests", true)]
    public void TelemetryDisabled_DoesNotRegisterTheMeterProvider(string environment, bool disabled)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["DISABLE_TELEMETRY"] = disabled.ToString(CultureInfo.InvariantCulture);

        builder.Services.AddPrintFarmerTelemetry(builder.Configuration, builder.Environment);

        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(MeterProvider));
    }

    private static async Task AcquireAsync(
        PrinterFileEnrichmentMetrics metrics, ManualClock clock, long milliseconds, bool timeout = false)
    {
        byte[] content = "; layer_height = 0.2\n"u8.ToArray();
        PrinterFileEnrichmentSources sources = new(null, (_, _, _, _) =>
        {
            clock.Advance(milliseconds);
            if (timeout)
            {
                throw new PrinterFileCopyException("deadline");
            }

            return Task.FromResult(new PrinterFileRegion(content, 0, true));
        });
        PrinterFileEnrichmentContent result = await PrinterFileEnrichmentAcquisition.AcquireAsync(
            new("private-storage", "private-file.gcode", content.Length, ModifiedValue: "1"),
            sources, false, CancellationToken.None, backend: PrinterBackend.PrusaLink, metrics: metrics);
        Assert.Equal(timeout ? PrinterFileEnrichmentState.Deferred : PrinterFileEnrichmentState.Ready, result.MetadataState);
    }

    private static void AssertSeries(string[] lines, string name, double value, params string[] labels)
    {
        string line = Assert.Single(lines, line =>
            line.StartsWith(name + "{", StringComparison.Ordinal) &&
            line.Contains("backend=\"prusalink\"", StringComparison.Ordinal) &&
            labels.All(label => line.Contains(label, StringComparison.Ordinal)));
        string sample = line[(line.LastIndexOf('}') + 1)..].Trim().Split(' ')[0];
        Assert.Equal(value, double.Parse(sample, CultureInfo.InvariantCulture));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => milliseconds;

        public void Advance(long elapsed) => milliseconds += elapsed;
    }
}
