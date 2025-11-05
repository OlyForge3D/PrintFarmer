using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
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
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();
        // Manually construct controller (controllers aren't added to root service provider in this test host)
        var repo = jobRepo;
        var evtSvc = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Slicing.ISliceJobEventService>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        ILogger<Farm.Web.Api.Controllers.Slicing.SliceJobController> logger = loggerFactory.CreateLogger<Farm.Web.Api.Controllers.Slicing.SliceJobController>();
        var hostEnv = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var profileRepo = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Repositories.Slicing.ISlicerProfileRepository>();
        var rateLimit = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.RateLimiting.IRateLimitService>();
        var metrics = new Farm.Web.Api.Services.Slicing.SliceJobMetrics();
        var workerAuth = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Workers.IWorkerAuthService>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Worker-Key"] = "test-worker-key";
        var controller = new Farm.Web.Api.Controllers.Slicing.SliceJobController(repo, evtSvc, logger, hostEnv, profileRepo, artifactsService, rateLimit, metrics, workerAuth)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // Create job in Processing state
        var job = new SliceJob
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
        var bytes = System.Text.Encoding.UTF8.GetBytes("; gcode content");
        var formFile = new TestFormFile(bytes, "primary.gcode", "application/gcode");
        var primary = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);

        // Complete with log text
        var request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = primary.Id,
            LogText = "Layer 1 OK\nLayer 2 OK"
        };
        var result = await controller.CompleteJobAsync(job.Id, request);

        // Validate response
        var ok = result as OkObjectResult;
        ok.Should().NotBeNull();
        var response = ok!.Value as CompleteSliceJobResponse;
        response.Should().NotBeNull();
        response!.ArtifactIds.Should().Contain(primary.Id);
        response.LogArtifactId.Should().NotBeNull();
        response.ArtifactIds.Should().Contain(response.LogArtifactId!.Value);

        // Verify artifact persisted
        var artifacts = await artifactsService.ListByJobAsync(job.Id, default);
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
