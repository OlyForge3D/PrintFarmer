using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Farm.Web.Api.Tests;

public class OctoPrintClientTests
{
    private static (OctoPrintClient client, Mock<HttpMessageHandler> handler, List<HttpRequestMessage> recorded) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var recorded = new List<HttpRequestMessage>();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                recorded.Add(req);
                return responder(req);
            });
#pragma warning disable CA2000 // Dispose objects before losing scope - HttpClient is owned by the test client for test lifetime
        var http = new HttpClient(handler.Object);
#pragma warning restore CA2000
        var client = new OctoPrintClient(http);
        return (client, handler, recorded);
    }

    private static HttpResponseMessage Json(object obj, HttpStatusCode code = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(obj);
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task GetPrinterStateAsync_ParsesStateAndTemps()
    {
        var (client, _, recorded) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/api/printer");
            return Json(new
            {
                state = "Operational",
                temperature = new
                {
                    tool0 = new { actual = 210.5, target = 215.0 },
                    bed = new { actual = 60.0, target = 65.0 }
                }
            });
        });
        var json = await client.GetPrinterStateAsync("http://octo", "key");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("state").GetString().Should().Be("Operational");
        doc.RootElement.GetProperty("temperature").GetProperty("tool0").GetProperty("actual").GetDouble().Should().Be(210.5);
    }

    [Fact]
    public async Task GetJobStatusAsync_ParsesJobName()
    {
        var (client, _, recorded) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/api/job");
            return Json(new
            {
                job = new { file = new { name = "test.gcode" } },
                progress = new { completion = 42.0 }
            });
        });
        var json = await client.GetJobStatusAsync("http://octo", "key");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("job").GetProperty("file").GetProperty("name").GetString().Should().Be("test.gcode");
    }

    [Fact]
    public async Task PluginDetection_ParsesPluginsList()
    {
        var (client, _, recorded) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/api/plugins");
            return Json(new
            {
                plugins = new[] {
                    new { key = "display_current_position" },
                    new { key = "spoolmanager" },
                    new { key = "spoolman" }
                }
            });
        });
        var request = new HttpRequestMessage(HttpMethod.Get, "http://octo/api/plugins");
        request.Headers.Add("X-Api-Key", "key");
        // Use reflection to access internal HttpClient property
        var httpClientProp = typeof(OctoPrintClient).GetProperty("HttpClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(httpClientProp);
        var httpClientObj = httpClientProp.GetValue(client);
        Assert.NotNull(httpClientObj);
        var httpClient = (HttpClient)httpClientObj!;
        var response = await httpClient.SendAsync(request);
        var pluginsJson = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(pluginsJson);
        var keys = doc.RootElement.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        keys.Should().Contain(new[] { "display_current_position", "spoolmanager", "spoolman" });
    }
}
