using System.Net;
using System.Text;
using System.Text.Json;

namespace Farm.Web.Api.Tests.Slicing;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class SlicingSubmissionControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SlicingSubmissionControllerTests(CustomWebApplicationFactory factory)
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Slice_ReturnsAccepted_WithJobId()
    {
        var content = new MultipartFormDataContent();
        // Minimal valid ASCII STL
        var stlBytes = Encoding.UTF8.GetBytes("solid test\nendsolid\n");
        content.Add(new ByteArrayContent(stlBytes), "model", "cube.stl");
        content.Add(new StringContent("prusaslicer"), "slicerEngine");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "printerId");
        content.Add(new StringContent("{\"layerHeight\":0.2,\"infillPercentage\":20,\"printSpeed\":60,\"nozzleTemperature\":210,\"bedTemperature\":60,\"supports\":false,\"material\":\"PLA\",\"quality\":\"standard\"}"), "slicerProfile");

        var response = await _client.PostAsync("/api/slicer/slice", content);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        body.TryGetProperty("jobId", out var jobIdProp).Should().BeTrue();
        jobIdProp.GetString().Should().NotBeNullOrWhiteSpace();
    }
}
// ReSharper restore RedundantUsingDirective
