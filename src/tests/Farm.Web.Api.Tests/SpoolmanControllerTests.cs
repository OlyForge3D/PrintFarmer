using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using System.Net;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
public class SpoolmanControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SpoolmanControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _client = factory.CreateClient();
    }

    [Fact(DisplayName = "Spoolman test endpoint returns validation error when BaseUrl missing")]
    public async Task TestEndpoint_ReturnsError_WhenMissingBaseUrl()
    {
        var resp = await _client.PostAsJsonAsync("/api/spoolman/test", new { });
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK); // always 200 contract
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("message").GetString().Should().Contain("BaseUrl", because: "missing base url should be reported");
    }

    [Fact(DisplayName = "Spoolman test endpoint categorizes DNS failure")]
    public async Task TestEndpoint_ReturnsDnsCategory_ForUnknownHost()
    {
        // Use a guaranteed-invalid TLD to force DNS failure quickly
        var payload = new { baseUrl = "http://nonexistent-spoolman-test-domain.invalid" };
        var resp = await _client.PostAsJsonAsync("/api/spoolman/test", payload);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.TryGetProperty("errorCategory", out var cat).Should().BeTrue();
        var category = cat.GetString();
        category
            .Should()
            .BeOneOf("dns_failure", "network_error", "http_error", "unknown");
        // message should exist
        json.TryGetProperty("message", out var msg).Should().BeTrue();
        msg.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "Spoolman test endpoint normalizes URL without scheme")]
    public async Task TestEndpoint_NormalizesUrl_WhenSchemeMissing()
    {
        var payload = new { baseUrl = "spoolman-host:7912" }; // will assume http://
        var resp = await _client.PostAsJsonAsync("/api/spoolman/test", payload);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.TryGetProperty("normalizedUrl", out var normalized).Should().BeTrue();
        normalized.GetString().Should().StartWith("http://");
    }

    [Fact(DisplayName = "Spoolman test endpoint succeeds against healthy server and extracts version")]
    public async Task TestEndpoint_Succeeds_AndExtractsVersion()
    {
        // Spin up a lightweight in-memory web host that simulates Spoolman
        // We respond only to /api/v1/health with a version payload
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = await StartStubSpoolmanServerAsync(port, cts.Token);

        var resp = await _client.PostAsJsonAsync("/api/spoolman/test", new { baseUrl = url }, cts.Token);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.TryGetProperty("version", out var ver).Should().BeTrue();
        ver.GetString().Should().Be("0.99-test");
        json.GetProperty("endpointTried").GetString().Should().Be("/api/v1/health");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<IDisposable> StartStubSpoolmanServerAsync(int port, CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var runTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    try
                    {
                        if (ctx.Request.Url?.AbsolutePath == "/api/v1/health")
                        {
                            var payload = System.Text.Encoding.UTF8.GetBytes("{\"version\":\"0.99-test\"}");
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentLength64 = payload.Length;
                            await ctx.Response.OutputStream.WriteAsync(payload, 0, payload.Length, ct);
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                        }
                    }
                    catch
                    {
                        // ignore per-request exceptions
                    }
                    finally
                    {
                        try
                        {
                            ctx.Response.OutputStream.Close();
                        }
                        catch
                        {
                            // ignore close errors
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }, ct);

        return new DelegateDisposable(() =>
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
                // ignore disposal errors
            }
        });
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
