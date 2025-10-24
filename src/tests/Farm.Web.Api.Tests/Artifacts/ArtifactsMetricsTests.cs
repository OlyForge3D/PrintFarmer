using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.Artifacts;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Artifacts;

[Collection("Artifacts")]
public class ArtifactsMetricsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ArtifactsMetricsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static IFormFile CreateFormFile(byte[] content, string fileName, string contentType)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    [Fact(DisplayName = "Upload increments counter and records histogram")]
    public async Task Upload_Increments_Counter_And_Records_Histogram()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        var metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

        var meterListener = new MeterListener();
        var counterValues = new List<long>();
        var histogramValues = new List<long>();

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "PrintFarmer.Artifacts")
            {
                if (instrument.Name == "printfarmer.artifacts.uploaded_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
                else if (instrument.Name == "printfarmer.artifacts.upload_bytes")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "printfarmer.artifacts.uploaded_total")
            {
                counterValues.Add(measurement);
            }
            else if (instrument.Name == "printfarmer.artifacts.upload_bytes")
            {
                histogramValues.Add(measurement);
            }
        });

        meterListener.Start();

        var jobId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("test artifact content");
        var file = CreateFormFile(content, "test.gcode", "application/x-gcode");

        // Act
        await service.UploadAsync(file, jobId, workerId, "gcode", CancellationToken.None);

        // Assert - Wait briefly for async metrics collection
        await Task.Delay(100);

        counterValues.Should().Contain(1, "upload counter should record increment of 1");
        histogramValues.Should().Contain(content.Length, "histogram should record exact file size");

        meterListener.Dispose();
    }

    [Fact(DisplayName = "Storage gauge reflects cumulative size")]
    public async Task Storage_Gauge_Reflects_Cumulative_Size()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        var metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

        var meterListener = new MeterListener();
        var gaugeValues = new List<long>();

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "PrintFarmer.Artifacts" &&
                instrument.Name == "printfarmer.artifacts.storage_total_bytes")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "printfarmer.artifacts.storage_total_bytes")
            {
                gaugeValues.Add(measurement);
            }
        });

        meterListener.Start();
        meterListener.RecordObservableInstruments();

        var initialGaugeValue = gaugeValues.LastOrDefault();

        var jobId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var content1 = Encoding.UTF8.GetBytes("first artifact");
        var content2 = Encoding.UTF8.GetBytes("second artifact with more content");

        // Act - Upload two artifacts
        var file1 = CreateFormFile(content1, "first.gcode", "application/x-gcode");
        await service.UploadAsync(file1, jobId, workerId, "gcode", CancellationToken.None);

        var file2 = CreateFormFile(content2, "second.png", "image/png");
        await service.UploadAsync(file2, jobId, workerId, "thumbnail", CancellationToken.None);

        // Observe gauge after uploads
        meterListener.RecordObservableInstruments();
        await Task.Delay(100);

        // Assert
        var finalGaugeValue = gaugeValues.Last();
        var expectedIncrease = content1.Length + content2.Length;

        (finalGaugeValue - initialGaugeValue).Should().Be(expectedIncrease,
            "gauge should reflect cumulative size of both uploads");

        meterListener.Dispose();
    }

    [Fact(DisplayName = "Multiple uploads increment counter correctly")]
    public async Task Multiple_Uploads_Increment_Counter_Correctly()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        var meterListener = new MeterListener();
        long counterTotal = 0;

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "PrintFarmer.Artifacts" &&
                instrument.Name == "printfarmer.artifacts.uploaded_total")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "printfarmer.artifacts.uploaded_total")
            {
                Interlocked.Add(ref counterTotal, measurement);
            }
        });

        meterListener.Start();

        var jobId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        // Act - Upload 5 artifacts
        for (int i = 0; i < 5; i++)
        {
            var content = Encoding.UTF8.GetBytes($"artifact {i}");
            var file = CreateFormFile(content, $"test{i}.gcode", "application/x-gcode");
            await service.UploadAsync(file, jobId, workerId, "gcode", CancellationToken.None);
        }

        await Task.Delay(100);

        // Assert
        counterTotal.Should().Be(5, "counter should increment for each upload");

        meterListener.Dispose();
    }
}
