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
[Collection(IntegrationTestCollection.Name)]
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

        // Retrieve status and verify ModelFileUrls round-trip
        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted!.JobId}");
        statusResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        status.Should().NotBeNull();
        status!.ModelFileUrls.Should().NotBeNull();
        status.ModelFileUrls.Should().HaveCount(3);
        status.ModelFileUrls.Should().ContainInOrder(modelUrls);

        _output.WriteLine($"Multi-model job {status.Id} stored {status.ModelFileUrls!.Count} URLs");
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

        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted!.JobId}");
        statusResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        status.Should().NotBeNull();
        status!.ModelFileUrls.Should().BeNull();
        status.ModelFileUrl.Should().Be("http://example.com/model.stl");

        _output.WriteLine($"Single-model job {status.Id} returned null ModelFileUrls (backward compat OK)");
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

        var submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted!.JobId}");
        var status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        status!.ModelFileUrls.Should().BeNull();
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

        HttpResponseMessage statusResp = await _client.GetAsync($"/api/slice/{submitted!.JobId}");
        var status = await statusResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();

        status!.ModelFileUrls.Should().NotBeNull();
        status.ModelFileUrls.Should().HaveCount(2);
        // All relative URLs should have been resolved to absolute
        foreach (string url in status.ModelFileUrls!)
        {
            url.Should().StartWith("http", "relative URLs should be resolved to absolute");
            url.Should().NotStartWith("/", "no relative URLs should remain");
        }

        _output.WriteLine($"Resolved URLs: {string.Join(", ", status.ModelFileUrls)}");
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

        // Claim the job
        var claimReq = new ClaimJobRequest
        {
            WorkerId = Guid.NewGuid(),
            Capabilities = ["orcaslicer"],
        };
        HttpResponseMessage claimResp = await _client.PostAsJsonAsync("/api/slice/claim", claimReq);
        claimResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var claimed = await claimResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        claimed.Should().NotBeNull();
        claimed!.ModelFileUrls.Should().NotBeNull();
        claimed.ModelFileUrls.Should().HaveCount(2);
        claimed.ModelFileUrls.Should().Contain("http://example.com/left.stl");
        claimed.ModelFileUrls.Should().Contain("http://example.com/right.stl");

        _output.WriteLine($"Claimed job {claimed.Id} has {claimed.ModelFileUrls!.Count} model URLs");
    }
}
