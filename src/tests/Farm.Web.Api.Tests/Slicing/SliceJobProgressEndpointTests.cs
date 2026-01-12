using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Tests /api/slice/{id}/progress endpoint updates job state and emits event prerequisites.
/// </summary>
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
            SlicerEngine = 0,
            SlicerProfileJson = "{}"
        };
        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submitReq);
        _ = submitResp.IsSuccessStatusCode.Should().BeTrue();
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        _ = submitted.Should().NotBeNull();

        // Claim job
        ClaimJobRequest claimReq = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" },
            LeaseDurationSeconds = 120
        };
        HttpResponseMessage claimResp = await _client.PostAsJsonAsync("/api/slice/claim", claimReq);
        _ = claimResp.IsSuccessStatusCode.Should().BeTrue();

        // Progress update
        SliceJobProgressUpdateRequest progressReq = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 42,
            ProgressMessage = "Layer slicing"
        };
        HttpResponseMessage progressResp = await _client.PostAsJsonAsync($"/api/slice/{submitted!.JobId}/progress", progressReq);
        _ = progressResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Fetch status
        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted.JobId}");
        _ = statusResp.IsSuccessStatusCode.Should().BeTrue();
        SliceJobStatusResponse? status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = status.Should().NotBeNull();
        _ = status!.ProgressPercent.Should().Be(42);
        _ = status.ProgressMessage.Should().Be("Layer slicing");
        _ = status.Status.Should().Be("Processing");
    }
}
