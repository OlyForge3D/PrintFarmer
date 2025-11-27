using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Web.Api.Controllers.Slicing;
using Farm.Web.Api.Services.Artifacts;
using Farm.Web.Api.Services.RateLimiting;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Api.Services.Workers;
using Farm.Web.Shared.Contracts.Slicing;
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
public class SliceJobCompletionLogTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public SliceJobCompletionLogTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact(DisplayName = "Completion endpoint persists log text as artifact when provided")]
    public async Task Completion_Persists_Log_Text_As_Artifact()
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();
        // Manually construct controller (controllers aren't added to root service provider in this test host)
        ISliceJobRepository repo = jobRepo;
        ISliceJobEventService evtSvc = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Slicing.ISliceJobEventService>();
        ILoggerFactory loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        ILogger<Farm.Web.Api.Controllers.Slicing.SliceJobController> logger = loggerFactory.CreateLogger<Farm.Web.Api.Controllers.Slicing.SliceJobController>();
        IHostEnvironment hostEnv = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        IProcessProfileRepository profileRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.IProcessProfileRepository>();
        IRateLimitService rateLimit = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.RateLimiting.IRateLimitService>();
        SliceJobMetrics metrics = new Farm.Web.Api.Services.Slicing.SliceJobMetrics();
        IWorkerAuthService workerAuth = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Workers.IWorkerAuthService>();
        DefaultHttpContext httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Worker-Key"] = "test-worker-key";
        SliceJobController controller = new Farm.Web.Api.Controllers.Slicing.SliceJobController(repo, evtSvc, logger, hostEnv, profileRepo, artifactsService, rateLimit, metrics, workerAuth)
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
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("; gcode content");
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
        ok.Should().NotBeNull();
        CompleteSliceJobResponse? response = ok!.Value as CompleteSliceJobResponse;
        response.Should().NotBeNull();
        response!.ArtifactIds.Should().Contain(primary.Id);
        response.LogArtifactId.Should().NotBeNull();
        response.ArtifactIds.Should().Contain(response.LogArtifactId!.Value);

        // Verify artifact persisted
        IReadOnlyList<Artifact> artifacts = await artifactsService.ListByJobAsync(job.Id, default);
        artifacts.Should().HaveCount(2);
        artifacts.Should().Contain(a => a.Kind == "log" && a.Id == response.LogArtifactId);
    }

    private sealed class TestFormFile : IFormFile
    {
        private readonly byte[] _data;
        public TestFormFile(byte[] data, string fileName, string contentType)
        {
            _data = data;
            FileName = fileName;
            ContentType = contentType;
            Name = "file";
            Length = data.Length;
        }
        public string ContentType { get; }
        public string ContentDisposition { get; set; } = string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length { get; }
        public string Name { get; }
        public string FileName { get; }
        public void CopyTo(System.IO.Stream target) => target.Write(_data, 0, _data.Length);
        public Task CopyToAsync(System.IO.Stream target, CancellationToken cancellationToken = default)
        {
            target.Write(_data, 0, _data.Length);
            return Task.CompletedTask;
        }
        public System.IO.Stream OpenReadStream() => new System.IO.MemoryStream(_data, writable: false);
    }
}
