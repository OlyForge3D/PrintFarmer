using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Integration tests for multi-extruder filament profile submission and persistence.
/// Validates that <c>ExtruderFilamentProfileNames</c> flows through the HTTP API,
/// gets embedded into <c>SlicerProfileJson</c>, and is persisted on the entity.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SliceJobMultiExtruderTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();
    private readonly ITestOutputHelper _output = output;
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

    [Fact(DisplayName = "Submit with ExtruderFilamentProfileNames persists array in SlicerProfileJson")]
    public async Task Submit_WithExtruderFilamentProfileNames_EmbedsInSlicerProfileJson()
    {
        var request = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/dual.stl",
            ModelFileName = "dual.stl",
            SlicerEngine = 0,
            SlicerProfileJson = """{"machineProfileName":"Dual Machine","filamentProfileName":"PLA","processProfileName":"Standard"}""",
            ExtruderFilamentProfileNames = ["Generic PLA @System", "Generic PETG @System"]
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", request);
        _ = submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();
        _ = submitted.Should().NotBeNull();
        _output.WriteLine($"Submitted multi-extruder job: {submitted!.JobId}");

        // Fetch the job back and verify the SlicerProfileJson has the array embedded
        HttpResponseMessage getResp = await _client.GetAsync($"/api/slice/{submitted.JobId}");
        _ = getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        SliceJobStatusResponse? status = await getResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();
        _ = status.Should().NotBeNull();
        _ = status!.SlicerProfileJson.Should().NotBeNullOrEmpty();

        using JsonDocument doc = JsonDocument.Parse(status.SlicerProfileJson!);
        JsonElement root = doc.RootElement;

        // Original properties preserved
        root.TryGetProperty("machineProfileName", out _).Should().BeTrue("original machineProfileName preserved");

        // Extruder filament names embedded
        root.TryGetProperty("extruderFilamentProfileNames", out JsonElement namesElem).Should().BeTrue("extruder names should be embedded");
        namesElem.GetArrayLength().Should().Be(2);
        namesElem[0].GetString().Should().Be("Generic PLA @System");
        namesElem[1].GetString().Should().Be("Generic PETG @System");
    }

    [Fact(DisplayName = "Submit without ExtruderFilamentProfileNames leaves SlicerProfileJson unchanged")]
    public async Task Submit_WithoutExtruderFilamentProfileNames_LeavesJsonUnchanged()
    {
        string originalJson = """{"machineProfileName":"Single Machine","filamentProfileName":"PLA","processProfileName":"Standard"}""";
        var request = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/single.stl",
            ModelFileName = "single.stl",
            SlicerEngine = 0,
            SlicerProfileJson = originalJson,
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", request);
        _ = submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        HttpResponseMessage getResp = await _client.GetAsync($"/api/slice/{submitted!.JobId}");
        SliceJobStatusResponse? status = await getResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();

        // Verify the JSON was not modified (no extruderFilamentProfileNames injected)
        using JsonDocument doc = JsonDocument.Parse(status!.SlicerProfileJson!);
        doc.RootElement.TryGetProperty("extruderFilamentProfileNames", out _).Should().BeFalse(
            "no extruder names should be added for single-extruder jobs");
    }

    [Fact(DisplayName = "Submit with ExtruderFilamentProfileNames already in JSON does not duplicate")]
    public async Task Submit_WithPreExistingExtruderNames_DoesNotDuplicate()
    {
        string jsonWithNames = """{"machineProfileName":"Dual","filamentProfileName":"PLA","extruderFilamentProfileNames":["PLA","PETG"]}""";
        var request = new SubmitSliceJobRequest
        {
            UserId = Guid.NewGuid(),
            ModelFileUrl = "http://example.com/dual2.stl",
            ModelFileName = "dual2.stl",
            SlicerEngine = 0,
            SlicerProfileJson = jsonWithNames,
            ExtruderFilamentProfileNames = ["PLA", "PETG"]
        };

        HttpResponseMessage submitResp = await _client.PostAsJsonAsync("/api/slice", request);
        _ = submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        SubmitSliceJobResponse? submitted = await submitResp.Content.ReadFromJsonAsync<SubmitSliceJobResponse>();

        HttpResponseMessage getResp = await _client.GetAsync($"/api/slice/{submitted!.JobId}");
        SliceJobStatusResponse? status = await getResp.Content.ReadFromJsonAsync<SliceJobStatusResponse>();

        // The JSON should still only have one extruderFilamentProfileNames array
        using JsonDocument doc = JsonDocument.Parse(status!.SlicerProfileJson!);
        JsonElement root = doc.RootElement;
        root.TryGetProperty("extruderFilamentProfileNames", out JsonElement elem).Should().BeTrue();
        elem.GetArrayLength().Should().Be(2, "should not duplicate the array");
    }
}
