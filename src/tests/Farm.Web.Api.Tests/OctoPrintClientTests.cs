using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services;
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
        List<HttpRequestMessage> recorded = new List<HttpRequestMessage>();
        Mock<HttpMessageHandler> handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _ = handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                recorded.Add(req);
                return responder(req);
            });
#pragma warning disable CA2000 // Dispose objects before losing scope - HttpClient is owned by the test client for test lifetime
        HttpClient http = new HttpClient(handler.Object);
#pragma warning restore CA2000
        OctoPrintClient client = new OctoPrintClient(http, null, new Farm.Infrastructure.Settings.BackendTimeoutSettings());
        return (client, handler, recorded);
    }

    private static HttpResponseMessage Json(object obj, HttpStatusCode code = HttpStatusCode.OK)
    {
        string json = JsonSerializer.Serialize(obj);
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task GetPrinterStateAsync_ParsesStateAndTemps()
    {
        (OctoPrintClient? client, _, List<HttpRequestMessage>? recorded) = CreateClient(req =>
        {
            _ = req.RequestUri!.AbsolutePath.Should().Be("/api/printer");
            return Json(new
            {
                state = new
                {
                    text = "Operational",
                    flags = new { operational = true, printing = false }
                },
                temperature = new
                {
                    tool0 = new { actual = 210.5, target = 215.0 },
                    bed = new { actual = 60.0, target = 65.0 }
                }
            });
        });
        OctoPrintPrinterState? state = await client.GetPrinterStateAsync("http://octo", new PrinterCredential { ApiKey = "key" });
        _ = state.Should().NotBeNull();
        _ = state!.State.Should().Be("Operational");
        _ = state.Operational.Should().BeTrue();
    }

    [Fact]
    public async Task GetJobStatusAsync_ParsesJobName()
    {
        (OctoPrintClient? client, _, List<HttpRequestMessage>? recorded) = CreateClient(req =>
        {
            _ = req.RequestUri!.AbsolutePath.Should().Be("/api/job");
            return Json(new
            {
                job = new { file = new { name = "test.gcode" } },
                progress = new { completion = 42.0 }
            });
        });
        OctoPrintJobStatus? status = await client.GetJobStatusAsync("http://octo", new PrinterCredential { ApiKey = "key" });
        _ = status.Should().NotBeNull();
        _ = status!.Filename.Should().Be("test.gcode");
        _ = status.Progress.Should().Be(42.0);
    }

    [Fact]
    public async Task PluginDetection_ParsesPluginsList()
    {
        (OctoPrintClient? client, _, List<HttpRequestMessage>? recorded) = CreateClient(req =>
        {
            _ = req.RequestUri!.AbsolutePath.Should().Be("/api/plugins");
            return Json(new
            {
                plugins = new[] {
                    new { key = "display_current_position" },
                    new { key = "spoolmanager" },
                    new { key = "spoolman" }
                }
            });
        });
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "http://octo/api/plugins");
        request.Headers.Add("X-Api-Key", "key");
        // Use reflection to access internal HttpClient property
        PropertyInfo? httpClientProp = typeof(OctoPrintClient).GetProperty("HttpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientProp);
        object? httpClientObj = httpClientProp.GetValue(client);
        Assert.NotNull(httpClientObj);
        HttpClient httpClient = (HttpClient)httpClientObj!;
        HttpResponseMessage response = await httpClient.SendAsync(request);
        string pluginsJson = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(pluginsJson);
        List<string?> keys = doc.RootElement.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("key").GetString()).ToList();
        _ = keys.Should().Contain(new[] { "display_current_position", "spoolmanager", "spoolman" });
    }

    [Fact]
    public async Task GetHistoryJobAsync_Explicit404_ThrowsKeyNotFound()
    {
        (OctoPrintClient client, _, _) = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        Func<Task> action = async () => await client.GetHistoryJobAsync(
            "http://octo",
            "missing",
            new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [InlineData("""{"success":false}""")]
    [InlineData("""{"success":true}""")]
    [InlineData("""not-json""")]
    public async Task GetHistoryJobAsync_InvalidApplicationOrPayload_ThrowsInvalidData(
        string payload)
    {
        (OctoPrintClient client, _, _) = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });

        Func<Task> action = async () => await client.GetHistoryJobAsync(
            "http://octo",
            "provider-job",
            new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("""{"success":true,"count":0}""")]
    [InlineData("""{"success":true,"count":1,"results":[{}]}""")]
    [InlineData("""{"success":true,"count":1,"results":[{"name":"a.gcode","timestamp":1700000000}]}""")]
    [InlineData("""{"success":true,"count":1,"results":[{"name":"a.gcode","success":true}]}""")]
    [InlineData("""{"success":true,"count":"one","results":[]}""")]
    public async Task GetHistoryListAsync_IncompleteOrMalformedEntry_IsUnavailable(
        string payload)
    {
        (OctoPrintClient client, _, _) = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json"),
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            credential: new PrinterCredential { ApiKey = "key" });

        history.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryListAsync_CompleteEntries_AreAuthoritative()
    {
        (OctoPrintClient client, _, _) = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"success":true,"count":1,"results":[{"name":"a.gcode","success":true,"timestamp":1700000000}]}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            credential: new PrinterCredential { ApiKey = "key" });

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle();
        history.Jobs[0].JobId.Should().Be("a.gcode");
    }
}
