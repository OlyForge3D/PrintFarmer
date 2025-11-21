using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Tests worker authentication enforcement on protected slicing endpoints.
/// </summary>
public class SliceJobWorkerAuthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SliceJobWorkerAuthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact(DisplayName = "Claim endpoint returns 401 when worker key header is missing")]
    public async Task Claim_Returns_401_When_Header_Missing()
    {
        // Arrange - create a queued job first
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        var request = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - attempt claim without header
        var response = await _client.PostAsJsonAsync("/api/slice/claim", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Claim endpoint returns 401 when worker key header is invalid")]
    public async Task Claim_Returns_401_When_Header_Invalid()
    {
        // Arrange - create a queued job first
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        var request = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - attempt claim with wrong key
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        var response = await _client.SendAsync(requestMessage);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Progress endpoint returns 401 when worker key header is missing")]
    public async Task Progress_Returns_401_When_Header_Missing()
    {
        // Arrange - create a processing job
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        var request = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 50,
            ProgressMessage = "Processing layers"
        };

        // Act - attempt progress update without header
        var response = await _client.PostAsJsonAsync($"/api/slice/{job.Id}/progress", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Progress endpoint returns 401 when worker key header is invalid")]
    public async Task Progress_Returns_401_When_Header_Invalid()
    {
        // Arrange - create a processing job
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        var request = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 50,
            ProgressMessage = "Processing layers"
        };

        // Act - attempt progress update with wrong key
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/progress")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        var response = await _client.SendAsync(requestMessage);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Completion endpoint returns 401 when worker key header is missing")]
    public async Task Completion_Returns_401_When_Header_Missing()
    {
        // Arrange - create a processing job with artifact
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-3),
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Create a dummy artifact using helper
        var bytes = System.Text.Encoding.UTF8.GetBytes("; gcode");
        var formFile = new TestFormFile(bytes, "output.gcode", "application/gcode");
        var artifact = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);

        var request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact.Id
        };

        // Act - attempt completion without header
        var response = await _client.PostAsJsonAsync($"/api/slice/{job.Id}/complete", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Completion endpoint returns 401 when worker key header is invalid")]
    public async Task Completion_Returns_401_When_Header_Invalid()
    {
        // Arrange - create a processing job with artifact
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var artifactsService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Artifacts.IArtifactsService>();

        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Processing,
            QueuedAt = DateTime.UtcNow.AddMinutes(-3),
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl",
            WorkerId = Guid.NewGuid()
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        // Create a dummy artifact using helper
        var bytes = System.Text.Encoding.UTF8.GetBytes("; gcode");
        var formFile = new TestFormFile(bytes, "output.gcode", "application/gcode");
        var artifact = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);

        var request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact.Id
        };

        // Act - attempt completion with wrong key
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        var response = await _client.SendAsync(requestMessage);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Claim endpoint succeeds with valid worker key")]
    public async Task Claim_Succeeds_With_Valid_Key()
    {
        // Arrange - create a queued job
        using var scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Repositories.Slicing.ISliceJobRepository>();
        var job = new Farm.Infrastructure.Domain.SliceJob
        {
            Id = Guid.NewGuid(),
            Status = Farm.Infrastructure.Domain.SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        var request = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - claim with valid key
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "test-worker-key");
        var response = await _client.SendAsync(requestMessage);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        result.Should().NotBeNull();
        result!.Id.Should().NotBeEmpty();
        result.Status.Should().Be(SliceJobStatus.Processing);
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
