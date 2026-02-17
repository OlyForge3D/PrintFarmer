using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.Artifacts;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Artifacts;

[Collection(IntegrationTestCollection.Name)]
public class ArtifactsMetricsTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    private static IFormFile CreateFormFile(byte[] content, string fileName, string contentType)
    {
        MemoryStream stream = new MemoryStream(content);
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
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        ArtifactsMetrics metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

        MeterListener meterListener = new MeterListener();
        List<long> counterValues = new List<long>();
        List<long> histogramValues = new List<long>();

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

        Guid jobId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes("test artifact content");
        IFormFile file = CreateFormFile(content, "test.gcode", "application/x-gcode");

        // Act
        _ = await service.UploadAsync(file, jobId, workerId, "gcode", CancellationToken.None);

        // Assert - Wait briefly for async metrics collection
        await Task.Delay(100);

        _ = counterValues.Should().Contain(1, "upload counter should record increment of 1");
        _ = histogramValues.Should().Contain(content.Length, "histogram should record exact file size");

        meterListener.Dispose();
    }

    [Fact(DisplayName = "Storage gauge reflects cumulative size")]
    public async Task Storage_Gauge_Reflects_Cumulative_Size()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        ArtifactsMetrics metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

        MeterListener meterListener = new MeterListener();
        List<long> gaugeValues = new List<long>();

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

        long initialGaugeValue = gaugeValues.LastOrDefault();

        Guid jobId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        byte[] content1 = Encoding.UTF8.GetBytes("first artifact");
        byte[] content2 = Encoding.UTF8.GetBytes("second artifact with more content");

        // Act - Upload two artifacts
        IFormFile file1 = CreateFormFile(content1, "first.gcode", "application/x-gcode");
        _ = await service.UploadAsync(file1, jobId, workerId, "gcode", CancellationToken.None);

        IFormFile file2 = CreateFormFile(content2, "second.png", "image/png");
        _ = await service.UploadAsync(file2, jobId, workerId, "thumbnail", CancellationToken.None);

        // Observe gauge after uploads
        meterListener.RecordObservableInstruments();
        await Task.Delay(100);

        // Assert
        long finalGaugeValue = gaugeValues.Last();
        int expectedIncrease = content1.Length + content2.Length;

        _ = (finalGaugeValue - initialGaugeValue).Should().Be(expectedIncrease,
            "gauge should reflect cumulative size of both uploads");

        meterListener.Dispose();
    }

    [Fact(DisplayName = "Multiple uploads increment counter correctly")]
    public async Task Multiple_Uploads_Increment_Counter_Correctly()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IArtifactsService service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        MeterListener meterListener = new MeterListener();
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
                _ = Interlocked.Add(ref counterTotal, measurement);
            }
        });

        meterListener.Start();

        Guid jobId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();

        // Act - Upload 5 artifacts
        for (int i = 0; i < 5; i++)
        {
            byte[] content = Encoding.UTF8.GetBytes($"artifact {i}");
            IFormFile file = CreateFormFile(content, $"test{i}.gcode", "application/x-gcode");
            _ = await service.UploadAsync(file, jobId, workerId, "gcode", CancellationToken.None);
        }

        await Task.Delay(100);

        // Assert
        _ = counterTotal.Should().Be(5, "counter should increment for each upload");

        meterListener.Dispose();
    }
}
