using System.Net;
using System.Text;

namespace Farm.Web.Api.Tests.Slicing;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class SlicingProgressControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SlicingProgressControllerTests(CustomWebApplicationFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Progress_Stream_StartsWithQueuedOrSlicing()
    {
        // First submit a job
        var content = new MultipartFormDataContent();
        var stlBytes = Encoding.UTF8.GetBytes("solid test\nendsolid\n");
        content.Add(new ByteArrayContent(stlBytes), "model", "cube.stl");
        content.Add(new StringContent("prusaslicer"), "slicerEngine");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "printerId");
        content.Add(new StringContent("{\"layerHeight\":0.2,\"infillPercentage\":20,\"printSpeed\":60,\"nozzleTemperature\":210,\"bedTemperature\":60,\"supports\":false,\"material\":\"PLA\",\"quality\":\"standard\"}"), "slicerProfile");
        var submitResponse = await _client.PostAsync("/api/slicer/slice", content);
        submitResponse.EnsureSuccessStatusCode();
        var body = await submitResponse.Content.ReadAsStringAsync();
        var jobId = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString();
        jobId.Should().NotBeNull();

        // Request progress SSE
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/slicer/progress/{jobId}");
        var progressResponse = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        progressResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // Read a small chunk to ensure stream begins
        var stream = await progressResponse.Content.ReadAsStreamAsync();
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);
        read.Should().BeGreaterThan(0);
        var chunk = Encoding.UTF8.GetString(buffer, 0, read);
        chunk.Should().Contain("data:");
        // Enum values are capitalized in the API payload, accept either Queued or Slicing
        chunk.Should().MatchRegex("\"status\":\"(Queued|Slicing)\"");
    }
}
