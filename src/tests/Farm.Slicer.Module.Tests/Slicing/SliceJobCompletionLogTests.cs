using System;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Services.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests inline log text handling in slice job completion endpoint.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobCompletionLogTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact(DisplayName = "Completion endpoint persists log text as artifact when provided")]
    public async Task Completion_Persists_Log_Text_As_Artifact()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        // Manually construct controller (controllers aren't added to root service provider in this test host)
        ISliceJobRepository repo = jobRepo;
        ISliceJobEventService evtSvc = scope.ServiceProvider.GetRequiredService<ISliceJobEventService>();
        ILoggerFactory loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        ILogger<SliceJobController> logger = loggerFactory.CreateLogger<SliceJobController>();
        IRateLimitService rateLimit = scope.ServiceProvider.GetRequiredService<IRateLimitService>();
        SliceJobMetrics metrics = new SliceJobMetrics();
        IWorkerAuthService workerAuth = scope.ServiceProvider.GetRequiredService<IWorkerAuthService>();
        IWorkerRepository workerRepository = scope.ServiceProvider.GetRequiredService<IWorkerRepository>();
        DefaultHttpContext httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Worker-Key"] = "test-worker-key";
        SliceJobController controller = new SliceJobController(repo, evtSvc, logger, artifactsService, rateLimit, metrics, workerAuth, workerRepository, Options.Create(new Farm.Slicer.Module.Settings.SlicerSettings()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // Create job in Processing state
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-3),
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Upload primary gcode artifact
        byte[] bytes = Encoding.UTF8.GetBytes("; gcode content");
        TestFormFile formFile = new TestFormFile(bytes, "primary.gcode", "application/gcode");
        Artifact primary = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);

        // Complete with log text
        CompleteSliceJobRequest request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = primary.Id,
            LogText = "Layer 1 OK\nLayer 2 OK"
        };
        IActionResult result = await controller.CompleteAsync(job.Id, request, default);

        // Validate response
        OkObjectResult? ok = result as OkObjectResult;
        _ = ok.Should().NotBeNull();
        CompleteSliceJobResponse? response = ok!.Value as CompleteSliceJobResponse;
        _ = response.Should().NotBeNull();
        _ = response!.ArtifactIds.Should().Contain(primary.Id);
        _ = response.LogArtifactId.Should().NotBeNull();
        _ = response.ArtifactIds.Should().Contain(response.LogArtifactId!.Value);

        // Verify artifact persisted
        IReadOnlyList<Artifact> artifacts = await artifactsService.ListByJobAsync(job.Id, default);
        _ = artifacts.Should().HaveCount(2);
        _ = artifacts.Should().Contain(a => a.Kind == "log" && a.Id == response.LogArtifactId);
    }

    private sealed class TestFormFile(byte[] data, string fileName, string contentType) : IFormFile
    {
        private readonly byte[] _data = data;

        public string ContentType { get; } = contentType;
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; } = data.Length;
        public string Name { get; } = "file";
        public string FileName { get; } = fileName;
        public void CopyTo(Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
    }
}
