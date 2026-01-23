using System;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Web.Api.Controllers.Slicing;
using Farm.Web.Api.Services.Artifacts;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Api.Services.Workers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

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
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();
        // Manually construct controller (controllers aren't added to root service provider in this test host)
        ISliceJobRepository repo = jobRepo;
        ISliceJobEventService evtSvc = scope.ServiceProvider.GetRequiredService<ISliceJobEventService>();
        ILoggerFactory loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        ILogger<SliceJobController> logger = loggerFactory.CreateLogger<SliceJobController>();
        IHostEnvironment hostEnv = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        IProcessProfileRepository profileRepo = scope.ServiceProvider.GetRequiredService<IProcessProfileRepository>();
        IRateLimitService rateLimit = scope.ServiceProvider.GetRequiredService<IRateLimitService>();
        SliceJobMetrics metrics = new SliceJobMetrics();
        IWorkerAuthService workerAuth = scope.ServiceProvider.GetRequiredService<IWorkerAuthService>();
        DefaultHttpContext httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Worker-Key"] = "test-worker-key";
        SliceJobController controller = new SliceJobController(repo, evtSvc, logger, hostEnv, profileRepo, artifactsService, rateLimit, metrics, workerAuth)
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
        IActionResult result = await controller.CompleteJobAsync(job.Id, request);

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
