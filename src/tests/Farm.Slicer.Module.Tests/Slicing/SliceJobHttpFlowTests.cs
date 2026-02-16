using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// HTTP-level integration test exercising the full public slice job HTTP flow:
/// Submit (POST /api/slice) -> Claim (POST /api/slice/claim) -> Get Status (GET /api/slice/{id}).
/// This validates the new worker bridging path (BLOCKER 1) independent of repository shortcuts.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobHttpFlowTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new CustomWebApplicationFactory();
    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;
    private HttpClient _client;

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

    [Fact(DisplayName = "HTTP flow: submit then claim transitions status to Processing")]
    public async Task Submit_Then_Claim_Transitions_Status()
    {
        // _client is already authenticated via InitializeAsync with Bearer token and X-Worker-Key header

        // 1. Submit new job
        SubmitSliceJobRequest submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(), // explicit to bypass NameIdentifier fallback path
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}"
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
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
        HttpResponseMessage claimResp = await _client.PostAsJsonAsync("/api/slice/claim", claimReq);
        string claimBody = await claimResp.Content.ReadAsStringAsync();
        _ = claimResp.StatusCode.Should().Be(HttpStatusCode.OK, $"Claim failed. Status {(int)claimResp.StatusCode}. Body: {claimBody}");
        SliceJobStatusResponse? claimed = await claimResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = claimed.Should().NotBeNull();
        _ = claimed!.Id.Should().Be(submitted.JobId);
        _ = claimed.Status.Should().Be("Processing");
        _ = claimed.WorkerId.Should().NotBeNull();

        // 3. Fetch status directly
        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted.JobId}");
        _ = statusResp.EnsureSuccessStatusCode();
        SliceJobStatusResponse? status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = status.Should().NotBeNull();
        _ = status!.Id.Should().Be(submitted.JobId);
        _ = status.Status.Should().Be("Processing");
        _ = status.WorkerId.Should().Be(claimed.WorkerId);
    }
}
