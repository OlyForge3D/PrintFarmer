using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests /api/slice/{id}/progress endpoint updates job state and emits event prerequisites.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobProgressEndpointTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client;

    public SliceJobProgressEndpointTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateWorkerClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "Progress endpoint updates percent and message for Processing job")]
    public async Task Progress_Updates_Fields()
    {
        // _client is already authenticated via InitializeAsync with Bearer token and X-Worker-Key header

        // Submit job
        SubmitSliceJobRequest submitReq = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = "{}"
        };
        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submitReq);
        _ = submitResp.IsSuccessStatusCode.Should().BeTrue();
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        _ = submitted.Should().NotBeNull();

        // Claim job
        ClaimJobRequest claimReq = new ClaimJobRequest
        {
            WorkerId = Guid.Parse(_client.DefaultRequestHeaders.GetValues("X-Worker-Id").Single()),
            Capabilities = new[] { "orcaslicer" },
            LeaseDurationSeconds = 120
        };
        HttpResponseMessage claimResp = await _client.PostAsJsonAsync("/api/slice/claim", claimReq);
        _ = claimResp.IsSuccessStatusCode.Should().BeTrue();
        WorkerSliceJobResponse? claimed = await claimResp.Content.ReadFromJsonAsync<WorkerSliceJobResponse>();
        _ = claimed.Should().NotBeNull();
        _ = claimed!.ClaimToken.Should().NotBe(Guid.Empty);
        _ = claimed!.LeaseToken.Should().NotBe(Guid.Empty);
        _ = claimed.LeaseFence.Should().BeGreaterThan(0);

        // Progress update presenting the claim-issued lease
        SliceJobProgressUpdateRequest progressReq = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 42,
            ProgressMessage = "Layer slicing"
        };
        using HttpRequestMessage progressMessage = new(HttpMethod.Post, $"/api/slice/{submitted!.JobId}/progress")
        {
            Content = JsonContent.Create(progressReq),
        };
        progressMessage.Headers.Add(WorkerClaimHeaders.ClaimToken, claimed.ClaimToken.ToString());
        progressMessage.Headers.Add(WorkerLeaseHeaders.LeaseToken, claimed.LeaseToken.ToString());
        progressMessage.Headers.Add(
            WorkerLeaseHeaders.LeaseFence,
            claimed.LeaseFence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        HttpResponseMessage progressResp = await _client.SendAsync(progressMessage);
        _ = progressResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Fetch status
        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted.JobId}");
        _ = statusResp.IsSuccessStatusCode.Should().BeTrue();
        SliceJobStatusResponse? status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = status.Should().NotBeNull();
        _ = status!.ProgressPercent.Should().Be(42);
        _ = status.ProgressMessage.Should().Be("Slicing in progress (42%).");
        _ = status.ProgressMessage.Should().NotContain("Layer slicing");
        _ = status.Status.Should().Be("Processing");
    }
}
