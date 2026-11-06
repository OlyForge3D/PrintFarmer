using System.Net;
using System.Security.Cryptography;
using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>Exercises multipart uploads over real Kestrel, which enforces request body limits.</summary>
public sealed class WorkerArtifactUploadSizeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(500_000_001)]
    public void ArtifactStorage_InvalidFileCeiling_FailsOptionsValidation(long maximumSize)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ArtifactStorage:MaxFileSizeBytes"] = maximumSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).Build();
        ServiceCollection services = new();
        services.AddSlicerApiServices(config);
        using ServiceProvider provider = services.BuildServiceProvider();
        Action readSettings = () => _ = provider.GetRequiredService<IOptions<ArtifactStorageSettings>>().Value;
        readSettings.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData(32 * 1024 * 1024, 100 * 1024 * 1024, true, HttpStatusCode.Created, true, false)]
    [InlineData(129 * 1024 * 1024, 140 * 1024 * 1024, true, HttpStatusCode.Created, true, false)]
    [InlineData(2048, 1024, true, HttpStatusCode.BadRequest, true, false)]
    [InlineData(2 * 1024 * 1024, 1024, true, HttpStatusCode.RequestEntityTooLarge, true, false)]
    [InlineData(16, 1024, false, HttpStatusCode.Unauthorized, true, false)]
    [InlineData(32 * 1024 * 1024, 100 * 1024 * 1024, true, HttpStatusCode.BadRequest, false, false)]
    [InlineData(16, 1024, true, HttpStatusCode.Created, true, true)]
    [InlineData(2048, 1024, true, HttpStatusCode.BadRequest, true, true)]
    public async Task UploadWorkerArtifact_ConfiguredLimitsAndCredentials_EnforcesPolicy(
        int fileSize,
        long maximumSize,
        bool authenticated,
        HttpStatusCode expectedStatus,
        bool enablePolicy,
        bool chunked)
    {
        await using WorkerArtifactApplicationFactory originalFactory = new();
        await using WebApplicationFactory<Program> factory = originalFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.ConfigureServices(services => services.PostConfigure<ArtifactStorageSettings>(
                settings => settings.MaxFileSizeBytes = maximumSize));
            if (!enablePolicy)
            {
                builder.ConfigureServices(services => services.Configure<MvcOptions>(
                    options => options.Conventions.Add(new DefaultServerLimitsConvention())));
            }
        });
        factory.UseKestrel(0);
        // Seed worker identity through the original factory, then use its credentials over Kestrel.
        using HttpClient seedClient = await originalFactory.CreateWorkerClientAsync();
        using HttpClient client = factory.CreateClient();
        if (authenticated)
        {
            foreach (var header in seedClient.DefaultRequestHeaders)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Worker worker = await db.Workers.SingleAsync();
        SliceJob job = new()
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            UserId = Guid.NewGuid(),
            ModelFileName = "large-artifact.stl",
            ModelFileUrl = "http://example/large-artifact.stl",
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            WorkerId = worker.Id,
            ClaimToken = Guid.NewGuid(),
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            LeaseToken = Guid.NewGuid(),
            LeaseFence = 1,
        };
        db.SliceJobs.Add(job);
        await db.SaveChangesAsync();

        byte[] bytes = new byte[fileSize];
        bytes[0] = (byte)';';
        bytes[^1] = (byte)'\n';
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(bytes), "file", "large.gcode");
        form.Add(new StringContent("gcode"), "kind");
        form.Add(new StringContent(hash), "sha256");
        form.Add(new StringContent(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), "sizeBytes");
        using HttpRequestMessage request = new(HttpMethod.Post, $"/api/slice/{job.Id}/artifacts")
        {
            Content = form,
        };
        request.Headers.Add(WorkerClaimHeaders.ClaimToken, job.ClaimToken!.Value.ToString());
        request.Headers.Add(WorkerLeaseHeaders.LeaseToken, job.LeaseToken!.Value.ToString());
        request.Headers.Add(WorkerLeaseHeaders.LeaseFence, "1");
        request.Headers.ExpectContinue = true;
        request.Headers.TransferEncodingChunked = chunked;
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(expectedStatus, await response.Content.ReadAsStringAsync());
        if (expectedStatus != HttpStatusCode.Created)
        {
            if (!enablePolicy)
            {
                (await response.Content.ReadAsStringAsync()).Should().Contain("30000000");
            }

            (await db.Artifacts.CountAsync(a => a.JobId == job.Id)).Should().Be(0);
            return;
        }

        Artifact artifact = await db.Artifacts.AsNoTracking().SingleAsync(a => a.JobId == job.Id);
        artifact.SizeBytes.Should().Be(bytes.Length);
        artifact.Sha256.Should().BeEquivalentTo(hash);
        artifact.WorkerId.Should().Be(worker.Id);
        artifact.ClaimToken.Should().Be(job.ClaimToken);
        Farm.Slicer.Module.Services.IArtifactsService storage =
            scope.ServiceProvider.GetRequiredService<Farm.Slicer.Module.Services.IArtifactsService>();
        await using Stream stored = (await storage.OpenReadStreamAsync(artifact.Id, CancellationToken.None))!.Content;
        Convert.ToHexString(await SHA256.HashDataAsync(stored)).ToLowerInvariant().Should().Be(hash);
    }

    private sealed class DefaultServerLimitsConvention : IActionModelConvention
    {
        public void Apply(ActionModel action)
        {
            foreach (WorkerArtifactUploadLimitsAttribute filter in action.Filters.OfType<WorkerArtifactUploadLimitsAttribute>().ToArray())
            {
                action.Filters.Remove(filter);
            }
        }
    }

    private sealed class WorkerArtifactApplicationFactory : CustomWebApplicationFactory
    {
        private readonly string _artifactRoot = Path.Join(Path.GetTempPath(), $"worker-upload-{Guid.NewGuid():N}");

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.PostConfigure<ArtifactStorageSettings>(settings => settings.RootPath = _artifactRoot);
            services.Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Warning);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_artifactRoot))
            {
                Directory.Delete(_artifactRoot, recursive: true);
            }
        }
    }
}
