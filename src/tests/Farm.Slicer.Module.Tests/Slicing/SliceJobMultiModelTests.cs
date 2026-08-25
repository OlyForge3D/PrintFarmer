using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Integration tests for multi-model slice job submission and retrieval.
/// Validates that ModelFileUrls are stored, resolved, and returned correctly,
/// while preserving backward compatibility for single-model jobs.
/// </summary>
public class SliceJobMultiModelTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;
    private HttpClient _client = null!;

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

    [Fact(DisplayName = "Submit with ModelFileUrls stores and returns multiple URLs")]
    public async Task SubmitMultiModel_StoresAndReturnsUrls()
    {
        List<string> modelUrls =
        [
            "http://example.com/part_a.stl",
            "http://example.com/part_b.stl",
            "http://example.com/part_c.3mf",
        ];

        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = modelUrls[0],
            ModelFileName = "part_a.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = modelUrls,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        submitted.Should().NotBeNull();

        WorkerSliceJobResponse claimed = await ClaimJobAsync();
        claimed.ModelFileUrls.Should().NotBeNull();
        claimed.ModelFileUrls.Should().HaveCount(3);
        claimed.ModelFileUrls.Should().OnlyContain(
            url => url.StartsWith($"/api/slice/{submitted!.JobId}/models/", StringComparison.Ordinal));

        _output.WriteLine($"Multi-model job {claimed.Id} exposed {claimed.ModelFileUrls!.Count} protected routes");
    }

    [Fact(DisplayName = "Submit without ModelFileUrls returns null (backward compat)")]
    public async Task SubmitSingleModel_ModelFileUrlsIsNull()
    {
        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        submitted.Should().NotBeNull();

        WorkerSliceJobResponse claimed = await ClaimJobAsync();
        claimed.ModelFileUrls.Should().BeNull();
        claimed.ModelFileUrl.Should().Be($"/api/slice/{submitted.JobId}/model");

        _output.WriteLine($"Single-model job {claimed.Id} returned one protected model route");
    }

    [Fact(DisplayName = "Submit with empty ModelFileUrls list returns null")]
    public async Task SubmitEmptyModelFileUrls_ReturnsNull()
    {
        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = [],
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);

        _ = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        WorkerSliceJobResponse claimed = await ClaimJobAsync();
        claimed.ModelFileUrls.Should().BeNull();
    }

    [Fact(DisplayName = "Submit with relative ModelFileUrls resolves them to absolute")]
    public async Task SubmitRelativeUrls_ResolvesToAbsolute()
    {
        List<string> relativeUrls =
        [
            "/api/models/files/abc.stl",
            "/api/models/files/def.3mf",
        ];

        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = relativeUrls[0],
            ModelFileName = "abc.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = relativeUrls,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        WorkerSliceJobResponse claimed = await ClaimJobAsync();

        claimed.ModelFileUrls.Should().NotBeNull();
        claimed.ModelFileUrls.Should().HaveCount(2);
        foreach (string url in claimed.ModelFileUrls!)
        {
            url.Should().StartWith($"/api/slice/{submitted!.JobId}/models/");
            url.Should().NotContain("models/files", "workers receive only identity-bound proxy routes");
        }

        _output.WriteLine($"Protected URLs: {string.Join(", ", claimed.ModelFileUrls)}");
    }

    [Fact(DisplayName = "Claimed multi-model job includes ModelFileUrls in status")]
    public async Task ClaimMultiModelJob_IncludesUrlsInResponse()
    {
        List<string> modelUrls =
        [
            "http://example.com/left.stl",
            "http://example.com/right.stl",
        ];

        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = modelUrls[0],
            ModelFileName = "left.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = modelUrls,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        submitted.Should().NotBeNull();

        // Claim the job
        var claimReq = new ClaimJobRequest
        {
            WorkerId = GetWorkerId(),
            Capabilities = ["orcaslicer"],
        };
        HttpResponseMessage claimResp = await _client.PostAsJsonAsync("/api/slice/claim", claimReq);
        claimResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var claimed = await claimResp.Content.ReadFromJsonAsync<WorkerSliceJobResponse>();
        claimed.Should().NotBeNull();
        claimed!.ModelFileUrls.Should().NotBeNull();
        claimed.ModelFileUrls.Should().HaveCount(2);
        claimed.ModelFileUrls.Should().Contain($"/api/slice/{submitted!.JobId}/models/0");
        claimed.ModelFileUrls.Should().Contain($"/api/slice/{submitted.JobId}/models/1");

        _output.WriteLine($"Claimed job {claimed.Id} has {claimed.ModelFileUrls!.Count} model URLs");
    }

    [Fact(DisplayName = "Submit with ModelFileTransforms stores and returns per-model transforms")]
    public async Task SubmitMultiModel_WithTransforms_StoresAndReturnsTransforms()
    {
        List<string> modelUrls =
        [
            "http://example.com/part_a.stl",
            "http://example.com/part_b.stl",
        ];

        List<string?> transforms =
        [
            """{"rotation":[0,0,0],"scale":[1,1,1],"position":[10,20,0]}""",
            """{"rotation":[1.5707963,0,0],"scale":[2,2,2],"position":[-15,30,0]}""",
        ];

        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = modelUrls[0],
            ModelFileName = "part_a.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = modelUrls,
            ModelFileTransforms = transforms,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        submitted.Should().NotBeNull();

        WorkerSliceJobResponse claimed = await ClaimJobAsync();
        claimed.ModelFileTransforms.Should().NotBeNull();
        claimed.ModelFileTransforms.Should().HaveCount(2);
        claimed.ModelFileTransforms![0].Should().Contain("\"position\":[10,20,0]");
        claimed.ModelFileTransforms[1].Should().Contain("\"rotation\":[1.5707963,0,0]");

        _output.WriteLine($"Multi-model job {claimed.Id} stored {claimed.ModelFileTransforms.Count} per-model transforms");
    }

    [Fact(DisplayName = "Submit without ModelFileTransforms returns null (backward compat)")]
    public async Task SubmitMultiModel_WithoutTransforms_ReturnsNull()
    {
        List<string> modelUrls =
        [
            "http://example.com/part_a.stl",
            "http://example.com/part_b.stl",
        ];

        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = modelUrls[0],
            ModelFileName = "part_a.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = modelUrls,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);

        _ = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        WorkerSliceJobResponse claimed = await ClaimJobAsync();
        claimed.ModelFileTransforms.Should().BeNull();
    }

    [Fact(DisplayName = "Submit rejects mismatched ModelFileTransforms and ModelFileUrls lengths")]
    public async Task SubmitMultiModel_MismatchedTransformsLength_ReturnsBadRequest()
    {
        List<string> modelUrls =
        [
            "http://example.com/part_a.stl",
            "http://example.com/part_b.stl",
        ];

        // Only one transform for two URLs
        List<string?> transforms =
        [
            """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""",
        ];

        var submit = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = modelUrls[0],
            ModelFileName = "part_a.stl",
            SlicerEngine = 0,
            SlicerProfileJson = "{}",
            ModelFileUrls = modelUrls,
            ModelFileTransforms = transforms,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", submit);
        submitResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<WorkerSliceJobResponse> ClaimJobAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/slice/claim",
            new ClaimJobRequest
            {
                WorkerId = GetWorkerId(),
                Capabilities = ["orcaslicer"],
            });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        WorkerSliceJobResponse? claimed =
            await response.Content.ReadFromJsonAsync<WorkerSliceJobResponse>();
        claimed.Should().NotBeNull();
        return claimed!;
    }

    private Guid GetWorkerId() =>
        Guid.Parse(_client.DefaultRequestHeaders.GetValues("X-Worker-Id").Single());
}
