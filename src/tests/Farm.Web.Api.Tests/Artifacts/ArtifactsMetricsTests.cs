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

        [Fact(DisplayName = "Upload increments counter and updates instance state")]
        public async Task Upload_Increments_Counter_And_Updates_Instance_State()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
            var metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

            var jobId = Guid.NewGuid();
            var workerId = Guid.NewGuid();
            var content = Encoding.UTF8.GetBytes("test artifact content");
            var file = CreateFormFile(content, "test.gcode", "application/x-gcode");

            var beforeCount = metrics.InstanceUploadedCount;
            var beforeSize = metrics.InstanceStorageBytes;

            // Diagnostic: print instance ids so we can correlate which instance received RecordUpload
            try
            {
                Console.WriteLine($"Test: scope metrics.InstanceId={metrics.InstanceId:N}, factory.HostArtifactsInstanceId={_factory.HostArtifactsInstanceId:N}");
            }
            catch { }

            // Act
            await service.UploadAsync(file, jobId, workerId, "gcode", CancellationToken.None);

            // Assert - Wait briefly for async updates
            await Task.Delay(100);

            metrics.InstanceUploadedCount.Should().Be(beforeCount + 1, "one upload should increment the instance counter");
            (metrics.InstanceStorageBytes - beforeSize).Should().Be(content.Length, "instance storage should increase by uploaded file size");
        }

        [Fact(DisplayName = "Storage gauge reflects cumulative size (instance)")]
        public async Task Storage_Gauge_Reflects_Cumulative_Size()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
            var metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

            var initialGaugeValue = metrics.InstanceStorageBytes;

            var jobId = Guid.NewGuid();
            var workerId = Guid.NewGuid();
            var content1 = Encoding.UTF8.GetBytes("first artifact");
            var content2 = Encoding.UTF8.GetBytes("second artifact with more content");

            // Act - Upload two artifacts
            var file1 = CreateFormFile(content1, "first.gcode", "application/x-gcode");
            await service.UploadAsync(file1, jobId, workerId, "gcode", CancellationToken.None);

            var file2 = CreateFormFile(content2, "second.png", "image/png");
            await service.UploadAsync(file2, jobId, workerId, "thumbnail", CancellationToken.None);

            await Task.Delay(100);

            // Assert
            var finalGaugeValue = metrics.InstanceStorageBytes;
            var expectedIncrease = content1.Length + content2.Length;

            (finalGaugeValue - initialGaugeValue).Should().Be(expectedIncrease,
                "instance storage should reflect cumulative size of both uploads");
        }

        [Fact(DisplayName = "Multiple uploads increment counter correctly (instance)")]
        public async Task Multiple_Uploads_Increment_Counter_Correctly()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
            var metrics = scope.ServiceProvider.GetRequiredService<ArtifactsMetrics>();

            var jobId = Guid.NewGuid();
            var workerId = Guid.NewGuid();

            // Capture pre-upload counter so test is tolerant to any uploads performed during host startup/seeding
            var beforeCount = metrics.InstanceUploadedCount;

            try
            {
                Console.WriteLine($"Test: scope metrics.InstanceId={metrics.InstanceId:N}, factory.HostArtifactsInstanceId={_factory.HostArtifactsInstanceId:N}");
            }
            catch { }

            // Act - Upload 5 artifacts
            for (int i = 0; i < 5; i++)
            {
                var content = Encoding.UTF8.GetBytes($"artifact {i}");
                var file = CreateFormFile(content, $"test{i}.gcode", "application/x-gcode");
                await service.UploadAsync(file, jobId, workerId, "gcode", CancellationToken.None);
            }

            await Task.Delay(100);

            try
            {
                Console.WriteLine($"Test (after): scope metrics.InstanceId={metrics.InstanceId:N}, factory.HostArtifactsInstanceId={_factory.HostArtifactsInstanceId:N}, InstanceUploadedCount={metrics.InstanceUploadedCount}");
            }
            catch { }

            // Assert - verify the delta from beforeCount is 5
            (metrics.InstanceUploadedCount - beforeCount).Should().Be(5, "instance counter should increment for each upload");
        }
}
