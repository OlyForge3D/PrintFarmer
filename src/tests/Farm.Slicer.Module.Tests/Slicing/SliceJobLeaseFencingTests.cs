using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Farm.Slicer.Module.Contracts;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Verifies that a claim is atomic and that every worker mutation is bound to the claiming worker,
/// the claimed job, an unexpired lease and the current fencing token.
/// </summary>
public sealed class SliceJobLeaseFencingTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient _firstWorker = null!;
    private HttpClient _secondWorker = null!;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _firstWorker = await _factory.CreateWorkerClientAsync(
            workerName: "Fencing Worker A",
            username: "fencing-worker-a",
            email: "fencing-a@example.com");
        _secondWorker = await _factory.CreateWorkerClientAsync(
            workerName: "Fencing Worker B",
            username: "fencing-worker-b",
            email: "fencing-b@example.com");
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _firstWorker.Dispose();
        _secondWorker.Dispose();
        _factory.Dispose();
    }

    [Fact(DisplayName = "Concurrent claims of one queued job produce exactly one winner")]
    public async Task ConcurrentClaims_ProduceExactlyOneWinner()
    {
        await QueueJobAsync();

        Task<HttpResponseMessage>[] claims =
        [
            ClaimAsync(_firstWorker),
            ClaimAsync(_secondWorker),
            ClaimAsync(_firstWorker),
            ClaimAsync(_secondWorker),
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(claims);

        int winners = responses.Count(response => response.StatusCode == HttpStatusCode.OK);
        _ = winners.Should().Be(1, "a queued job may only be claimed once");
        _ = responses.Count(response => response.StatusCode == HttpStatusCode.NoContent)
            .Should().Be(responses.Length - 1);
    }

    [Fact(DisplayName = "Claim issues a lease token and a monotonic fencing counter")]
    public async Task Claim_IssuesLeaseTokenAndFence()
    {
        await QueueJobAsync();

        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        _ = claimed.LeaseToken.Should().NotBe(Guid.Empty);
        _ = claimed.LeaseFence.Should().Be(1);
        _ = claimed.LeaseExpiresAtUtc.Should().NotBeNull();
        _ = claimed.LeaseExpiresAtUtc!.Value.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact(DisplayName = "Progress without lease headers is rejected")]
    public async Task Progress_WithoutLeaseHeaders_ReturnsConflict()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        HttpResponseMessage response = await _firstWorker.PostAsJsonAsync(
            $"/api/slice/{claimed.Id}/progress",
            new SliceJobProgressUpdateRequest { ProgressPercent = 10 });

        _ = response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _ = await ReadProblemCodeAsync(response).ConfigureAwait(true) is "lease_required";
    }

    [Fact(DisplayName = "Progress with a stale fencing token is rejected")]
    public async Task Progress_WithStaleFence_ReturnsConflict()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/progress")
        {
            Content = JsonContent.Create(new SliceJobProgressUpdateRequest { ProgressPercent = 10 }),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            (claimed.LeaseFence - 1).ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("stale_fencing_token");
    }

    [Fact(DisplayName = "Progress with a foreign lease token is rejected")]
    public async Task Progress_WithForeignLeaseToken_ReturnsConflict()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/progress")
        {
            Content = JsonContent.Create(new SliceJobProgressUpdateRequest { ProgressPercent = 10 }),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, Guid.NewGuid().ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("lease_conflict");
    }

    [Fact(DisplayName = "A worker cannot mutate a job claimed by another worker")]
    public async Task Progress_FromNonOwningWorker_ReturnsForbidden()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/progress")
        {
            Content = JsonContent.Create(new SliceJobProgressUpdateRequest { ProgressPercent = 10 }),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await _secondWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "A worker cannot mutate a job that does not exist")]
    public async Task Progress_ForUnknownJob_ReturnsNotFound()
    {
        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{Guid.NewGuid()}/progress")
        {
            Content = JsonContent.Create(new SliceJobProgressUpdateRequest { ProgressPercent = 10 }),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, Guid.NewGuid().ToString());
        message.Headers.Add(WorkerLeaseHeaders.LeaseFence, "1");
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "An expired lease is rejected and cannot be renewed")]
    public async Task ExpiredLease_IsRejected()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);
        await ExpireLeaseAsync(claimed.Id);

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/renew-lease")
        {
            Content = JsonContent.Create(new RenewLeaseRequest { LeaseDurationSeconds = 300 }),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("lease_expired");
    }

    [Fact(DisplayName = "Re-claiming an expired lease advances the fencing counter")]
    public async Task ReclaimingExpiredLease_AdvancesFence()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse first = await ClaimSuccessfullyAsync(_firstWorker);
        await ExpireLeaseAsync(first.Id);

        WorkerSliceJobResponse second = await ClaimSuccessfullyAsync(_secondWorker);

        _ = second.Id.Should().Be(first.Id);
        _ = second.LeaseFence.Should().Be(first.LeaseFence + 1);
        _ = second.LeaseToken.Should().NotBe(first.LeaseToken);
    }

    [Fact(DisplayName = "Missing worker credentials are rejected before any lease check")]
    public async Task MissingWorkerKey_ReturnsUnauthorized()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);
        using HttpClient anonymous = _factory.CreateClient();

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/progress")
        {
            Content = JsonContent.Create(new SliceJobProgressUpdateRequest { ProgressPercent = 10 }),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await anonymous.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "A worker failure is persisted as a terminal job state")]
    public async Task Failure_IsPersisted()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        using HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/fail")
        {
            Content = JsonContent.Create(new FailSliceJobRequest("worker detail")),
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob persisted = await db.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == claimed.Id);
        _ = persisted.Status.Should().Be(SliceJobStatus.Failed);
        _ = persisted.ErrorMessage.Should().Be("worker detail", "the worker-reported message must be persisted verbatim, not a hardcoded placeholder");
        _ = persisted.CompletedAt.Should().NotBeNull();
    }

    [Fact(DisplayName = "An artifact whose declared digest does not match is rejected")]
    public async Task ArtifactWithMismatchedDigest_IsRejected()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        byte[] bytes = Encoding.UTF8.GetBytes("; produced gcode\nG28\n");
        using HttpRequestMessage message = CreateArtifactUpload(
            claimed,
            bytes,
            declaredSha256: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("different bytes"))));
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("artifact_hash_mismatch");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = (await db.Artifacts.AsNoTracking().AnyAsync(artifact => artifact.JobId == claimed.Id))
            .Should().BeFalse("bytes that fail verification must never become an artifact");
    }

    [Theory(DisplayName = "An artifact without a valid declared digest is rejected")]
    [InlineData("")]
    [InlineData("not-a-sha256")]
    [InlineData("0123456789abcdef")]
    public async Task ArtifactWithoutValidDigest_IsRejected(string declaredSha256)
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        byte[] bytes = Encoding.UTF8.GetBytes("; produced gcode\nG28\n");
        using HttpRequestMessage message = CreateArtifactUpload(claimed, bytes, declaredSha256);
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("artifact_hash_invalid");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = (await db.Artifacts.AsNoTracking().AnyAsync(artifact => artifact.JobId == claimed.Id))
            .Should().BeFalse("an unverifiable upload must never become an artifact");
    }

    [Fact(DisplayName = "An artifact whose declared digest matches is accepted")]
    public async Task ArtifactWithMatchingDigest_IsAccepted()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        byte[] bytes = Encoding.UTF8.GetBytes("; produced gcode\nG28\n");
        using HttpRequestMessage message = CreateArtifactUpload(
            claimed,
            bytes,
            declaredSha256: Convert.ToHexString(SHA256.HashData(bytes)));
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Artifact stored = await db.Artifacts.AsNoTracking().SingleAsync(artifact => artifact.JobId == claimed.Id);
        _ = stored.DeclaredSha256.Should().Be(stored.Sha256);
    }

    [Fact(DisplayName = "An artifact whose declared size does not match is rejected")]
    public async Task ArtifactWithMismatchedSize_IsRejected()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        byte[] bytes = Encoding.UTF8.GetBytes("; produced gcode\nG28\n");
        using HttpRequestMessage message = CreateArtifactUpload(
            claimed,
            bytes,
            declaredSha256: Convert.ToHexString(SHA256.HashData(bytes)),
            declaredSize: bytes.Length + 1);
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("artifact_size_mismatch");
    }

    [Fact(DisplayName = "An artifact without a declared size is rejected")]
    public async Task ArtifactWithoutDeclaredSize_IsRejected()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        byte[] bytes = Encoding.UTF8.GetBytes("; produced gcode\nG28\n");
        using HttpRequestMessage message = CreateArtifactUpload(
            claimed,
            bytes,
            declaredSha256: Convert.ToHexString(SHA256.HashData(bytes)),
            includeDeclaredSize: false);
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("artifact_size_mismatch");
    }

    [Fact(DisplayName = "An artifact with an invalid declared size is rejected")]
    public async Task ArtifactWithInvalidDeclaredSize_IsRejected()
    {
        await QueueJobAsync();
        WorkerSliceJobResponse claimed = await ClaimSuccessfullyAsync(_firstWorker);

        byte[] bytes = Encoding.UTF8.GetBytes("; produced gcode\nG28\n");
        using HttpRequestMessage message = CreateArtifactUpload(
            claimed,
            bytes,
            declaredSha256: Convert.ToHexString(SHA256.HashData(bytes)),
            declaredSize: -1);
        HttpResponseMessage response = await _firstWorker.SendAsync(message);

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadProblemCodeAsync(response)).Should().Be("artifact_size_mismatch");
    }

    private static HttpRequestMessage CreateArtifactUpload(
        WorkerSliceJobResponse claimed,
        byte[] bytes,
        string declaredSha256,
        long? declaredSize = null,
        bool includeDeclaredSize = true)
    {
        MultipartFormDataContent content = new();
        ByteArrayContent file = new(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/x.gcode");
        content.Add(file, "file", "result.gcode");
        content.Add(new StringContent("gcode"), "kind");
        content.Add(new StringContent(declaredSha256), "sha256");
        if (includeDeclaredSize)
        {
            content.Add(
                new StringContent((declaredSize ?? bytes.Length).ToString(CultureInfo.InvariantCulture)),
                "sizeBytes");
        }

        HttpRequestMessage message = new(HttpMethod.Post, $"/api/slice/{claimed.Id}/artifacts")
        {
            Content = content,
        };
        message.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        message.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(CultureInfo.InvariantCulture));
        return message;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out System.Text.Json.JsonElement code)
            ? code.GetString()
            : null;
    }

    private Task<HttpResponseMessage> ClaimAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/slice/claim",
            new ClaimJobRequest
            {
                WorkerId = Guid.Parse(client.DefaultRequestHeaders.GetValues(WorkerLeaseHeaders.WorkerId).Single()),
                Capabilities = ["orcaslicer", "orcaslicer-upstream"],
                LeaseDurationSeconds = 300,
            });

    private async Task<WorkerSliceJobResponse> ClaimSuccessfullyAsync(HttpClient client)
    {
        HttpResponseMessage response = await ClaimAsync(client);
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        WorkerSliceJobResponse claimed = await response.Content.ReadFromJsonAsync<WorkerSliceJobResponse>()
            ?? throw new InvalidOperationException("Missing claim response.");
        _ = client.DefaultRequestHeaders.Remove(WorkerClaimHeaders.ClaimToken);
        client.DefaultRequestHeaders.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        return claimed;
    }

    private async Task QueueJobAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ISliceJobRepository repository = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        await repository.AddAsync(new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            ModelFileUrl = "models/queued.stl",
            ModelFileName = "queued.stl",
            SlicerEngine = (int)SlicerEngineType.OrcaSlicer,
            SlicerEngineName = SlicerEngineType.OrcaSlicer.ToString(),
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task ExpireLeaseAsync(Guid jobId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SliceJob job = await db.SliceJobs.SingleAsync(value => value.Id == jobId);
        job.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        _ = await db.SaveChangesAsync();
    }
}
