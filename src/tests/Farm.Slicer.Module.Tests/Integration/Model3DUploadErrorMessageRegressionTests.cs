using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Regression tests for issue #2215: malformed model uploads previously surfaced only an
/// opaque "Bad Request" to the user. These tests pin the exact HTTP response body shape for
/// the two reproduction scenarios in that issue (a structurally invalid .stl and an empty
/// .3mf), so a future change can't silently regress back to a bodyless/generic 400.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Regression")]
public class Model3DUploadErrorMessageRegressionTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public Model3DUploadErrorMessageRegressionTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private static MultipartFormDataContent CreateUpload(string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", fileName);
        return form;
    }

    [Fact]
    public async Task UploadModel_MalformedStlContents_ReturnsSpecificValidationMessage()
    {
        // Reproduction step 2 from #2215: an .stl-named file whose contents are not a valid
        // STL model (plain garbage bytes, no "solid" ASCII header, no matching binary size).
        byte[] garbage = Encoding.UTF8.GetBytes("this is not an STL file at all, just plain text padding to exceed the 84 byte header check");
        using MultipartFormDataContent form = CreateUpload("malformed.stl", garbage);

        HttpResponseMessage response = await _client!.PostAsync("/api/3d-models/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a structurally unreadable STL must be rejected, not silently accepted");

        string body = await response.Content.ReadAsStringAsync();
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().Be("application/json",
            $"error responses must be JSON per repo convention, but got Content-Type '{contentType}' with body: {body}");

        JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(body);
        parsed.TryGetProperty("message", out JsonElement messageElement).Should().BeTrue(
            "the frontend's parseXhrErrorMessage reads a top-level camelCase 'message' field");

        string message = messageElement.GetString()!;
        message.Should().NotBe("Bad Request",
            "REGRESSION #2215: the response body must carry a specific reason, not the opaque status text");
        message.Should().Contain("malformed.stl",
            "the message should name the rejected file so the user can identify it in the queue");
        message.Should().Contain("No triangles found in mesh",
            "the message must explain what was structurally wrong with the file, not just that " +
            "'validation' failed generically");
    }

    [Fact]
    public async Task UploadModel_EmptyThreeMfFile_ReturnsActionableMessage()
    {
        // Reproduction step 3 from #2215: an empty .3mf file.
        using MultipartFormDataContent form = CreateUpload("empty.3mf", []);

        HttpResponseMessage response = await _client!.PostAsync("/api/3d-models/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an empty upload must be rejected");

        string body = await response.Content.ReadAsStringAsync();
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.Should().Be("application/json",
            $"error responses must be JSON per repo convention, but got Content-Type '{contentType}' with body: {body}");

        JsonElement parsed = JsonSerializer.Deserialize<JsonElement>(body);
        parsed.TryGetProperty("message", out JsonElement messageElement).Should().BeTrue(
            "the frontend's parseXhrErrorMessage reads a top-level camelCase 'message' field");

        string message = messageElement.GetString()!;
        message.Should().NotBe("Bad Request",
            "REGRESSION #2215: the response body must carry a specific reason, not the opaque status text");
        message.Should().MatchRegex("empty|no file",
            "the message must explain that the uploaded file was empty");
    }
}
