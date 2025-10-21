using System;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Web.Shared.Contracts.Slicing;
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
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Worker-Key", "test-worker-key");

        // 1. Submit new job
        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(), // explicit to bypass NameIdentifier fallback path
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}"
        };

        var submitResp = await client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);
        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        submitted.Should().NotBeNull();
        submitted!.JobId.Should().NotBe(Guid.Empty);
        submitted.Status.Should().Be("Queued");

        // 2. Claim via worker endpoint
        var claimReq = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = new[] { "orcaslicer" },
            LeaseDurationSeconds = 300
        };
        var claimResp = await client.PostAsJsonAsync("/api/slice/claim", claimReq);
        var claimBody = await claimResp.Content.ReadAsStringAsync();
        claimResp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, $"Claim failed. Status {(int)claimResp.StatusCode}. Body: {claimBody}");
        var claimed = await claimResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(submitted.JobId);
        claimed.Status.Should().Be("Processing");
        claimed.WorkerId.Should().NotBeNull();

        // 3. Fetch status directly
        var statusResp = await client.GetAsync($"/api/slice/{submitted.JobId}");
        statusResp.EnsureSuccessStatusCode();
        var status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        status.Should().NotBeNull();
        status!.Id.Should().Be(submitted.JobId);
        status.Status.Should().Be("Processing");
        status.WorkerId.Should().Be(claimed.WorkerId);
    }
}
