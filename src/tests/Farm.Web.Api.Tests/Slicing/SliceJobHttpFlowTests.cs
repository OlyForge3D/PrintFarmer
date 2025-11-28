using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// HTTP-level integration test exercising the full public slice job HTTP flow:
/// Submit (POST /api/slice) -> Claim (POST /api/slice/claim) -> Get Status (GET /api/slice/{id}).
/// This validates the new worker bridging path (BLOCKER 1) independent of repository shortcuts.
/// </summary>
public class SliceJobHttpFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public SliceJobHttpFlowTests(CustomWebApplicationFactory factory, Xunit.Abstractions.ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact(DisplayName = "HTTP flow: submit then claim transitions status to Processing")]
    public async Task Submit_Then_Claim_Transitions_Status()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Worker-Key", "test-worker-key");

        // 1. Submit new job
        SubmitSliceJobRequest submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(), // explicit to bypass NameIdentifier fallback path
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}"
        };

        HttpResponseMessage submitResp = await client.PostAsJsonAsync("/api/slice", submit);
        _ = submitResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        _ = submitted.Should().NotBeNull();
        _ = submitted!.JobId.Should().NotBe(Guid.Empty);
        _ = submitted.Status.Should().Be("Queued");

        // 2. Claim via worker endpoint
        ClaimJobRequest claimReq = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" },
            LeaseDurationSeconds = 300
        };
        HttpResponseMessage claimResp = await client.PostAsJsonAsync("/api/slice/claim", claimReq);
        string claimBody = await claimResp.Content.ReadAsStringAsync();
        _ = claimResp.StatusCode.Should().Be(HttpStatusCode.OK, $"Claim failed. Status {(int)claimResp.StatusCode}. Body: {claimBody}");
        SliceJobStatusResponse? claimed = await claimResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = claimed.Should().NotBeNull();
        _ = claimed!.Id.Should().Be(submitted.JobId);
        _ = claimed.Status.Should().Be("Processing");
        _ = claimed.WorkerId.Should().NotBeNull();

        // 3. Fetch status directly
        HttpResponseMessage statusResp = await client.GetAsync($"/api/slice/{submitted.JobId}");
        _ = statusResp.EnsureSuccessStatusCode();
        SliceJobStatusResponse? status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = status.Should().NotBeNull();
        _ = status!.Id.Should().Be(submitted.JobId);
        _ = status.Status.Should().Be("Processing");
        _ = status.WorkerId.Should().Be(claimed.WorkerId);
    }
}
