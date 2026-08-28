using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests worker authentication enforcement on protected slicing endpoints.
/// </summary>
public class SliceJobWorkerAuthTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public SliceJobWorkerAuthTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
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
        Guid serviceId = await _factory.RegisterWorkerAsync(
            "registered-worker-key",
            "Wrong Key Worker");

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
            WorkerId = serviceId,
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - attempt claim with wrong key
        using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        requestMessage.Headers.Add("X-Worker-Id", serviceId.ToString());
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
        Guid serviceId = await _factory.RegisterWorkerAsync(
            "registered-worker-key",
            "Wrong Key Worker");

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
        using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/progress")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "wrong-key-value");
        requestMessage.Headers.Add("X-Worker-Id", serviceId.ToString());
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
        using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/slice/{job.Id}/complete")
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
        Guid serviceId = await _factory.RegisterWorkerAsync("test-worker-key", "Test Worker");

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
            WorkerId = serviceId,
            Capabilities = new[] { "orcaslicer" }
        };

        // Act - claim with valid key (uses authenticated client from InitializeAsync)
        // Note: _client already has Bearer token from CreateAuthenticatedClientAsync in InitializeAsync
        using HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(request)
        };
        requestMessage.Headers.Add("X-Worker-Key", "test-worker-key");
        requestMessage.Headers.Add("X-Worker-Id", serviceId.ToString());
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

    [Fact(DisplayName = "Claim ignores forged request capabilities when registration is generic")]
    public async Task Claim_WithForgedCapabilitiesAndGenericRegistration_ReturnsNoContent()
    {
        const string capabilities = """
            {
              "capabilities": ["orcaslicer"],
              "engineVersion": "2.3.1",
              "slicerDistribution": "upstream",
              "slicerVersion": "2.3.1",
              "slicerBinarySha256": "9f2c1b0a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a",
              "slicerContainerDigest": "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
              "realBinary": true
            }
            """;
        Guid serviceId = await _factory.RegisterWorkerAsync(
            "generic-worker-key",
            "Generic Worker",
            capabilities,
            "2.3.1");
        _ = await SeedPinnedJobAsync(await GetWorkerIdAsync(serviceId));

        HttpResponseMessage response = await ClaimAsync(serviceId, "generic-worker-key");

        _ = response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact(DisplayName = "Claim rejects a placeholder current worker version")]
    public async Task Claim_WithCurrentVersionRegistration_ReturnsNoContent()
    {
        const string capabilities = """
            {
              "capabilities": ["orcaslicer", "orcaslicer-upstream"],
              "engineVersion": "current",
              "slicerDistribution": "upstream",
              "slicerVersion": "current",
              "slicerBinarySha256": "9f2c1b0a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a",
              "slicerContainerDigest": "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
              "realBinary": true
            }
            """;
        Guid serviceId = await _factory.RegisterWorkerAsync(
            "current-worker-key",
            "Current Worker",
            capabilities,
            "current");
        _ = await SeedPinnedJobAsync(await GetWorkerIdAsync(serviceId));

        HttpResponseMessage response = await ClaimAsync(serviceId, "current-worker-key");

        _ = response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact(DisplayName = "Claim rejects an attestation with a missing binary digest")]
    public async Task Claim_WithMissingBinaryDigestRegistration_ReturnsNoContent()
    {
        const string capabilities = """
            {
              "capabilities": ["orcaslicer", "orcaslicer-upstream"],
              "engineVersion": "2.3.1",
              "slicerDistribution": "upstream",
              "slicerVersion": "2.3.1",
              "slicerContainerDigest": "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
              "realBinary": true
            }
            """;
        Guid serviceId = await _factory.RegisterWorkerAsync(
            "missing-digest-key",
            "Missing Digest Worker",
            capabilities,
            "2.3.1");
        _ = await SeedPinnedJobAsync(await GetWorkerIdAsync(serviceId));

        HttpResponseMessage response = await ClaimAsync(serviceId, "missing-digest-key");

        _ = response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact(DisplayName = "A matching attested worker claims and completes its pinned job")]
    public async Task Claim_AndCompletion_WithMatchingAttestation_Succeed()
    {
        const string capabilities = """
            {
              "capabilities": ["orcaslicer", "orcaslicer-upstream"],
              "engineVersion": "2.3.1",
              "slicerDistribution": "upstream",
              "slicerVersion": "2.3.1",
              "slicerBinarySha256": "9f2c1b0a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a",
              "slicerContainerDigest": "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
              "realBinary": true
            }
            """;
        const string workerKey = "attested-worker-key";
        Guid serviceId = await _factory.RegisterWorkerAsync(
            workerKey,
            "Attested Worker",
            capabilities,
            "2.3.1");
        Guid workerId = await GetWorkerIdAsync(serviceId);
        Guid jobId = await SeedPinnedJobAsync(workerId);

        HttpResponseMessage claimResponse = await ClaimAsync(serviceId, workerKey);
        _ = claimResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        WorkerSliceJobResponse claimed = (await claimResponse.Content
            .ReadFromJsonAsync<WorkerSliceJobResponse>())!;
        Guid artifactId = Guid.NewGuid();
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            _ = db.Artifacts.Add(new Artifact
            {
                Id = artifactId,
                JobId = jobId,
                WorkerId = workerId,
                ClaimToken = claimed.ClaimToken,
                Kind = "gcode",
                FileName = "output.gcode",
                RelativePath = $"{artifactId:N}.gcode",
                ContentType = "text/x.gcode",
                SizeBytes = 1,
                Sha256 = new string('A', 64),
                DeclaredSha256 = new string('A', 64),
                CreatedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync();
        }

        using HttpRequestMessage complete = new(HttpMethod.Post, $"/api/slice/{jobId}/complete")
        {
            Content = JsonContent.Create(new CompleteSliceJobRequest
            {
                PrimaryArtifactId = artifactId,
            }),
        };
        complete.Headers.Authorization = _client.DefaultRequestHeaders.Authorization;
        complete.Headers.Add("X-Worker-Key", workerKey);
        complete.Headers.Add("X-Worker-Id", serviceId.ToString());
        complete.Headers.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        complete.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        complete.Headers.Add(WorkerLeaseHeaders.LeaseFence, claimed.LeaseFence.ToString());

        HttpResponseMessage completionResponse = await _client.SendAsync(complete);

        _ = completionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using IServiceScope verificationScope = _factory.Services.CreateScope();
        SlicerDbContext verification =
            verificationScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob persisted = await verification.SliceJobs.AsNoTracking()
            .SingleAsync(job => job.Id == jobId);
        _ = persisted.Status.Should().Be(SliceJobStatus.Completed);
        _ = persisted.WorkerId.Should().Be(workerId);
        _ = persisted.ArtifactIdsCsv.Should().Be(artifactId.ToString());
    }

    private async Task<Guid> SeedPinnedJobAsync(Guid pinnedWorkerId)
    {
        using IServiceScope scope = _factory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        ISliceJobRepository repository = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        SliceJob job = new()
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "calibration.3mf",
            ModelFileUrl = "storage://calibration.3mf",
            SlicerEngineName = "OrcaSlicer",
            RequiredCapabilitiesJson = "[\"orcaslicer-upstream\"]",
            SlicerDistribution = "upstream",
            SlicerVersion = "2.3.1",
            SlicerContainerDigest =
                "sha256:0f5c6a6f1b1c4a1cbb2b0f1a1e8c2b1e3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
            PinnedWorkerId = pinnedWorkerId,
            SlicerBinarySha256 =
                "9f2c1b0a8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a",
        };
        await repository.AddAsync(job);
        await repository.SaveChangesAsync();
        return job.Id;
    }

    private async Task<Guid> GetWorkerIdAsync(Guid serviceId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        return await db.Workers
            .Where(worker => worker.ServiceId == serviceId.ToString())
            .Select(worker => worker.Id)
            .SingleAsync();
    }

    private async Task<HttpResponseMessage> ClaimAsync(Guid serviceId, string workerKey)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(new ClaimJobRequest
            {
                WorkerId = serviceId,
                Capabilities = ["orcaslicer", "orcaslicer-upstream"],
            }),
        };
        message.Headers.Add("X-Worker-Key", workerKey);
        message.Headers.Add("X-Worker-Id", serviceId.ToString());
        message.Headers.Authorization = _client.DefaultRequestHeaders.Authorization;
        return await _client.SendAsync(message);
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
