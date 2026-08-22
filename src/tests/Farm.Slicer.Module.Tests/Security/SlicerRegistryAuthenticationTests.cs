using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests.Security;

public sealed class SlicerRegistryAuthenticationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-shared-key")]
    public async Task RegisterAsync_MissingOrInvalidSharedKey_ReturnsUnauthorized(string? sharedKey)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = CreateRegistrationRequest("unauthorized-worker", sharedKey);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-shared-key")]
    public async Task ListAsync_MissingOrInvalidSharedKey_ReturnsUnauthorized(string? sharedKey)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/slicers");
        if (!string.IsNullOrEmpty(sharedKey))
        {
            request.Headers.Add("X-Slicer-Api-Key", sharedKey);
        }

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceRoutes_KeyForDifferentService_ReturnUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService first = await RegisterAsync(client, "first-worker");
        RegisteredService second = await RegisterAsync(client, "second-worker");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/slicers/{first.Id}");
        request.Headers.Add("X-Slicer-Service-Api-Key", second.ApiKey);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Fact]
    public async Task ServiceRoutes_MatchingServiceKey_ReturnRedactedServiceStatus()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService registered = await RegisterAsync(client, "authorized-worker");
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/slicers/{registered.Id}");
        request.Headers.Add("X-Slicer-Service-Api-Key", registered.ApiKey);

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        string normalizedBody = body.ToLowerInvariant();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = normalizedBody.Should().NotContain("apikey");
        _ = normalizedBody.Should().NotContain("host");
        _ = normalizedBody.Should().NotContain("capabilities");
    }

    [Fact]
    public async Task ListAsync_ValidSharedKey_ReturnsRedactedServiceStatuses()
    {
        using HttpClient client = _factory.CreateClient();
        _ = await RegisterAsync(client, "listed-worker");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/slicers");
        request.Headers.Add("X-Slicer-Api-Key", "test-worker-key");

        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        string normalizedBody = body.ToLowerInvariant();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = normalizedBody.Should().NotContain("apikey");
        _ = normalizedBody.Should().NotContain("host");
        _ = normalizedBody.Should().NotContain("capabilities");
    }

    [Fact]
    public async Task RegisterAsync_SameInstanceId_UpsertsSameWorkerAndRotatesCredential()
    {
        // Re-registering with the same InstanceId (e.g. a redeployed worker) must
        // reuse the existing service/worker record rather than creating a duplicate
        // (issue #1528). The API key still rotates on every registration, so the
        // previous credential is no longer valid once the newer one is issued.
        //
        // A live (non-Offline, recently-heartbeated) worker's InstanceId can no longer
        // be re-registered over without first going through a legitimate
        // deregister/heartbeat-timeout transition to Offline (issue #1860) — simulate
        // that here exactly like the redeploy fixtures above, so this test still models
        // a genuine redeploy rather than the squatting attack #1860 now rejects.
        using HttpClient client = _factory.CreateClient();
        RegisteredService first = await RegisterAsync(
            client,
            "first-replica",
            instanceId: "shared-diagnostic-instance");

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            Worker worker = db.Set<Worker>().Single(w => w.ServiceId == first.Id.ToString());
            worker.Status = WorkerStatus.Offline;
            await db.SaveChangesAsync();
        }

        RegisteredService second = await RegisterAsync(
            client,
            "second-replica",
            instanceId: "shared-diagnostic-instance");

        _ = second.Id.Should().Be(first.Id, "re-registering the same InstanceId must upsert the existing worker record");
        _ = second.ApiKey.Should().NotBe(first.ApiKey, "each registration still rotates the credential");
        await AssertWorkerCredentialStatusAsync(client, first, HttpStatusCode.Unauthorized);
        await AssertWorkerCredentialAcceptedAsync(client, second);
    }

    [Fact]
    public async Task WorkerRoutes_OfflineWorkerCredential_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService registered = await RegisterAsync(client, "offline-worker");
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            Worker worker = db.Set<Worker>().Single(w => w.ServiceId == registered.Id.ToString());
            worker.Status = WorkerStatus.Offline;
            await db.SaveChangesAsync();
        }

        await AssertWorkerCredentialStatusAsync(
            client,
            registered,
            HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ServiceRoutes_OfflineWorkerCredential_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService registered = await RegisterAsync(client, "offline-service");
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            Worker worker = db.Set<Worker>().Single(w => w.ServiceId == registered.Id.ToString());
            worker.Status = WorkerStatus.Offline;
            await db.SaveChangesAsync();
        }

        using HttpRequestMessage heartbeat = new(
            HttpMethod.Post,
            $"/api/slicers/{registered.Id}/heartbeat")
        {
            Content = JsonContent.Create(new HeartbeatDto
            {
                Status = WorkerStatus.Online,
                FreeSlots = 1,
            }),
        };
        heartbeat.Headers.Add("X-Slicer-Service-Api-Key", registered.ApiKey);

        HttpResponseMessage response = await client.SendAsync(heartbeat);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WorkerRoutes_DeregisteredWorkerCredential_ReturnsUnauthorized()
    {
        using HttpClient client = _factory.CreateClient();
        RegisteredService registered = await RegisterAsync(client, "deregistered-worker");
        using HttpRequestMessage deregister = new(
            HttpMethod.Post,
            $"/api/slicers/{registered.Id}/deregister");
        deregister.Headers.Add("X-Slicer-Service-Api-Key", registered.ApiKey);

        HttpResponseMessage deregisterResponse = await client.SendAsync(deregister);

        _ = deregisterResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertWorkerCredentialStatusAsync(
            client,
            registered,
            HttpStatusCode.Unauthorized);
    }

    private static async Task<RegisteredService> RegisterAsync(
        HttpClient client,
        string name,
        string? instanceId = null)
    {
        using HttpRequestMessage request = CreateRegistrationRequest(
            name,
            "test-worker-key",
            instanceId);
        HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        using JsonDocument document = JsonDocument.Parse(body);
        return new RegisteredService(
            document.RootElement.GetProperty("id").GetGuid(),
            document.RootElement.GetProperty("apiKey").GetString()
                ?? throw new InvalidOperationException("Registration did not return a service API key."));
    }

    private static async Task AssertWorkerCredentialAcceptedAsync(
        HttpClient client,
        RegisteredService registered)
    {
        await AssertWorkerCredentialStatusAsync(
            client,
            registered,
            HttpStatusCode.NoContent);
    }

    private static async Task AssertWorkerCredentialStatusAsync(
        HttpClient client,
        RegisteredService registered,
        HttpStatusCode expectedStatus)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/slice/claim")
        {
            Content = JsonContent.Create(new ClaimJobRequest
            {
                WorkerId = registered.Id,
                Capabilities = ["orcaslicer"],
            }),
        };
        request.Headers.Add("X-Worker-Id", registered.Id.ToString());
        request.Headers.Add("X-Worker-Key", registered.ApiKey);

        HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(expectedStatus);
    }

    private static HttpRequestMessage CreateRegistrationRequest(
        string name,
        string? sharedKey,
        string? instanceId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/slicers/register")
        {
            Content = JsonContent.Create(new RegisterSlicerDto
            {
                Name = name,
                SlicerType = (int)SlicerType.OrcaSlicer,
                Version = "2.3.1",
                Host = "http://private-worker.internal",
                CapabilitiesJson = """{"capabilities":["orcaslicer","orcaslicer-upstream"]}""",
                MaxConcurrentJobs = 1,
                InstanceId = instanceId ?? name,
            }),
        };
        if (!string.IsNullOrEmpty(sharedKey))
        {
            request.Headers.Add("X-Slicer-Api-Key", sharedKey);
        }

        return request;
    }

    private sealed record RegisteredService(Guid Id, string ApiKey);
}
