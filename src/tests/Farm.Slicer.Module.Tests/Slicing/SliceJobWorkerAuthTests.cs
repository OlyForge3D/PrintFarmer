using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.Artifacts;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests worker authentication enforcement on protected slicing endpoints.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobWorkerAuthTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client;

    public SliceJobWorkerAuthTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "Claim endpoint returns 401 when worker key header is missing")]
    public async Task Claim_Returns_401_When_Header_Missing()
    {
        // Arrange - create a queued job first
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        ClaimJobRequest request = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - attempt claim without header
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/slice/claim", request);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Claim endpoint returns 401 when worker key header is invalid")]
    public async Task Claim_Returns_401_When_Header_Invalid()
    {
        // Arrange - create a queued job first
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        ClaimJobRequest request = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - attempt claim with wrong key
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        HttpResponseMessage response = await _client.SendAsync(requestMessage);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Progress endpoint returns 401 when worker key header is missing")]
    public async Task Progress_Returns_401_When_Header_Missing()
    {
        // Arrange - create a processing job
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
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

        SliceJobProgressUpdateRequest request = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 50,
            ProgressMessage = "Processing layers"
        };

        // Act - attempt progress update without header
        HttpResponseMessage response = await _client.PostAsJsonAsync($"/api/slice/{job.Id}/progress", request);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Progress endpoint returns 401 when worker key header is invalid")]
    public async Task Progress_Returns_401_When_Header_Invalid()
    {
        // Arrange - create a processing job
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
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

        SliceJobProgressUpdateRequest request = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 50,
            ProgressMessage = "Processing layers"
        };

        // Act - attempt progress update with wrong key
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/progress")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        HttpResponseMessage response = await _client.SendAsync(requestMessage);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Completion endpoint returns 401 when worker key header is missing")]
    public async Task Completion_Returns_401_When_Header_Missing()
    {
        // Arrange - create a processing job with artifact
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
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
        byte[] bytes = Encoding.UTF8.GetBytes("; gcode");
        TestFormFile formFile = new TestFormFile(bytes, "output.gcode", "application/gcode");
        Artifact artifact = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);

        CompleteSliceJobRequest request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact.Id
        };

        // Act - attempt completion without header
        HttpResponseMessage response = await _client.PostAsJsonAsync($"/api/slice/{job.Id}/complete", request);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Completion endpoint returns 401 when worker key header is invalid")]
    public async Task Completion_Returns_401_When_Header_Invalid()
    {
        // Arrange - create a processing job with artifact
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        IArtifactsService artifactsService = scope.ServiceProvider.GetRequiredService<IArtifactsService>();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
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
        byte[] bytes = Encoding.UTF8.GetBytes("; gcode");
        TestFormFile formFile = new TestFormFile(bytes, "output.gcode", "application/gcode");
        Artifact artifact = await artifactsService.UploadAsync(formFile, job.Id, null, "gcode", default);

        CompleteSliceJobRequest request = new CompleteSliceJobRequest
        {
            PrimaryArtifactId = artifact.Id
        };

        // Act - attempt completion with wrong key
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        HttpResponseMessage response = await _client.SendAsync(requestMessage);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Claim endpoint succeeds with valid worker key")]
    public async Task Claim_Succeeds_With_Valid_Key()
    {
        // Register a valid worker in the database
        await _factory.RegisterWorkerAsync("test-worker-key", "Test Worker");

        // Arrange - create a queued job
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository jobRepo = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "http://example/model.stl"
        };
        await jobRepo.AddAsync(job);
        await jobRepo.SaveChangesAsync();

        ClaimJobRequest request = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - claim with valid key (uses authenticated client from InitializeAsync)
        // Note: _client already has Bearer token from CreateAuthenticatedClientAsync in InitializeAsync
        HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "test-worker-key");
        // Manually add auth header since SendAsync bypasses default headers
        AuthenticationHeaderValue? authHeader = _client.DefaultRequestHeaders.Authorization;
        if (authHeader != null)
        {
            requestMessage.Headers.Authorization = authHeader;
        }
        HttpResponseMessage response = await _client.SendAsync(requestMessage);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        SliceJobStatusResponse? result = await response.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = result.Should().NotBeNull();
        _ = result!.Id.Should().NotBeEmpty();
        _ = result.Status.Should().Be(SliceJobStatus.Processing);
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
