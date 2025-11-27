using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Web.Shared.Contracts.Slicing;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Tests /api/slice/{id}/progress endpoint updates job state and emits event prerequisites.
/// </summary>
public class SliceJobProgressEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public SliceJobProgressEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact(DisplayName = "Progress endpoint updates percent and message for Processing job")]
    public async Task Progress_Updates_Fields()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Worker-Key", "test-worker-key");

        // Submit job
        SubmitSliceJobRequest submitReq = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}"
        };
        HttpResponseMessage submitResp = await client.PostAsJsonAsync("/api/slice", submitReq);
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
        HttpResponseMessage claimResp = await client.PostAsJsonAsync("/api/slice/claim", claimReq);
        _ = claimResp.IsSuccessStatusCode.Should().BeTrue();

        // Progress update
        SliceJobProgressUpdateRequest progressReq = new SliceJobProgressUpdateRequest
        {
            ProgressPercent = 42,
            ProgressMessage = "Layer slicing"
        };
        HttpResponseMessage progressResp = await client.PostAsJsonAsync($"/api/slice/{submitted!.JobId}/progress", progressReq);
        _ = progressResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Fetch status
        HttpResponseMessage statusResp = await client.GetAsync($"/api/slice/{submitted.JobId}");
        _ = statusResp.IsSuccessStatusCode.Should().BeTrue();
        SliceJobStatusResponse? status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = status.Should().NotBeNull();
        _ = status!.ProgressPercent.Should().Be(42);
        _ = status.ProgressMessage.Should().Be("Layer slicing");
        _ = status.Status.Should().Be("Processing");
    }
}
