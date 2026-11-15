using System.Diagnostics;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.Integration;

[Trait("Category", "Performance")]
public sealed class Model3DPickerBenchmarkEvidenceTests : IAsyncLifetime, IDisposable
{
    private const int WarmupRequests = 5;
    private const int MeasuredRequests = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CountingDbCommandInterceptor _commandInterceptor = new();
    private readonly PickerBenchmarkWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public Model3DPickerBenchmarkEvidenceTests(ITestOutputHelper output)
    {
        _output = output;
        _factory = new PickerBenchmarkWebApplicationFactory(_commandInterceptor);
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task MeasureFullAndPickerLists_ForRepresentativeLibrarySizes()
    {
        foreach (int modelCount in new[] { 10, 100, 1000 })
        {
            await SeedModelsAsync(modelCount);
            Measurement[] fullList = await MeasureAsync(modelCount, picker: false);
            Measurement[] pickerList = await MeasureAsync(modelCount, picker: true);

            WriteSummary(modelCount, "full", fullList);
            WriteSummary(modelCount, "picker", pickerList);
        }
    }

    private async Task<Measurement[]> MeasureAsync(int modelCount, bool picker)
    {
        int expectedCommands = picker ? 1 : 1 + (2 * modelCount);
        List<Measurement> samples = new(MeasuredRequests);

        for (int request = 0; request < WarmupRequests + MeasuredRequests; request++)
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            IModel3DFileService service = scope.ServiceProvider.GetRequiredService<IModel3DFileService>();
            _commandInterceptor.Reset();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            Stopwatch stopwatch = Stopwatch.StartNew();

            object response = picker
                ? await service.ListPickerModelsAsync(CancellationToken.None)
                : await service.ListModelsAsync(CancellationToken.None);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);

            stopwatch.Stop();
            long allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
            string[] listCommands = GetListCommands(_commandInterceptor.Commands);
            Assert.True(
                expectedCommands == listCommands.Length,
                $"n={modelCount}, picker={picker}: expected {expectedCommands} commands, " +
                $"observed {listCommands.Length}:{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    listCommands
                        .GroupBy(command => command)
                        .Select(group => $"{group.Count()}x {group.Key}")));
            if (request >= WarmupRequests)
            {
                samples.Add(new Measurement(
                    listCommands.Length,
                    body.Length,
                    stopwatch.Elapsed.TotalMilliseconds,
                    allocatedBytes));
            }
        }

        return samples.ToArray();
    }

    private static string[] GetListCommands(IReadOnlyList<string> commands) =>
        commands
            .Where(command =>
                command.Contains("FROM \"Models3D\"", StringComparison.OrdinalIgnoreCase) ||
                command.Contains("FROM \"GcodeFiles\"", StringComparison.OrdinalIgnoreCase) ||
                command.Contains("FROM \"Model3DTag\"", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private async Task SeedModelsAsync(int modelCount)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        await db.Models3D.ExecuteDeleteAsync();

        DateTime baseUploadedAt = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Guid[] uploaderIds = [Guid.NewGuid(), Guid.NewGuid()];
        Model3D[] models = Enumerable.Range(0, modelCount)
            .Select(index =>
            {
                Guid id = Guid.NewGuid();
                return new Model3D
                {
                    Id = id,
                    Name = $"benchy-{index:D4}.3mf",
                    FileName = $"{id:N}.3mf",
                    FilePath = "/benchmarks/models",
                    FileHash = id.ToString("N"),
                    FileSizeBytes = 16_384 + index,
                    FileFormat = ModelFileFormat.TMF,
                    UploadedAt = baseUploadedAt.AddMinutes(index),
                    UploadedByUserId = uploaderIds[index % uploaderIds.Length],
                    IsValid = true,
                    ExtractedMetadataJson = """{"Title":"Benchmark model","Designer":"PrintFarmer"}""",
                    SourceUrl = "https://example.test/models/benchmark",
                    SourceLicense = "CC BY 4.0",
                    SourceCreator = $"creator-{index % 10}",
                    ImportedAt = baseUploadedAt,
                    CreatedAt = baseUploadedAt,
                    UpdatedAt = baseUploadedAt,
                };
            })
            .ToArray();

        db.Models3D.AddRange(models);
        await db.SaveChangesAsync();
    }

    private void WriteSummary(int modelCount, string label, IReadOnlyList<Measurement> samples)
    {
        double[] elapsed = samples.Select(sample => sample.ElapsedMilliseconds).Order().ToArray();
        long[] allocations = samples.Select(sample => sample.AllocatedBytes).Order().ToArray();
        _output.WriteLine(
            $"n={modelCount} {label}: commands/request={samples[0].CommandCount}; " +
            $"body-bytes={samples[0].BodyBytes}; latency-ms p50={Percentile(elapsed, 0.50):F3} " +
            $"p95={Percentile(elapsed, 0.95):F3}; allocated-bytes p50={Percentile(allocations, 0.50)} " +
            $"p95={Percentile(allocations, 0.95)}; samples={samples.Count}");
    }

    private static double Percentile(double[] sortedValues, double percentile) =>
        sortedValues[Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Length) - 1)];

    private static long Percentile(long[] sortedValues, double percentile) =>
        sortedValues[Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Length) - 1)];

    private sealed record Measurement(
        int CommandCount,
        int BodyBytes,
        double ElapsedMilliseconds,
        long AllocatedBytes);

    private sealed class PickerBenchmarkWebApplicationFactory(
        CountingDbCommandInterceptor commandInterceptor) : CustomWebApplicationFactory
    {
        protected override Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? CommandInterceptor =>
            commandInterceptor;

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            ServiceDescriptor? artifactCleanupService = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(ArtifactCleanupHostedService));
            if (artifactCleanupService is not null)
            {
                services.Remove(artifactCleanupService);
            }
        }
    }
}
