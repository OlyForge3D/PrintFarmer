using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Tests.TestHelpers;
using Farm.Web.Shared;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class SpoolmanServiceTests
{
    [Fact]
    public async Task ListSpoolsAsync_ReturnsEmpty_WhenNotConfigured()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        // Returning null intentionally for this test case. Suppress CS8603 for this line.
#pragma warning disable CS8603 // Possible null reference return
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(() => (SpoolmanSettings?)null);
#pragma warning restore CS8603 // Possible null reference return

        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();
        using FakeHttpMessageHandler _handler = new FakeHttpMessageHandler();
        using HttpClient http = new HttpClient(_handler);

        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        IReadOnlyList<SpoolmanSpoolDto> result = await svc.ListSpoolsAsync(CancellationToken.None);

        Assert.Empty(result);
        logger.Verify(l => l.LogDebug(It.Is<string>(m => m.Contains("Spoolman not configured")), null, null), Times.Once);
    }

    [Fact]
    public async Task ListSpoolsAsync_PaginatesAcrossNextUrls()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        // First page returns a 'next' field pointing to second page; second page returns final array
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
                {
                    if (req.RequestUri!.AbsolutePath.StartsWith("/api/v1/spool") || req.RequestUri!.AbsolutePath.StartsWith("/api/v1/spools"))
                    {
                        if (req.RequestUri!.Query.Contains("page=2"))
                        {
                            string json2 = JsonSerializer.Serialize(new[] { new { id = 2, name = "Second" } });
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(json2, Encoding.UTF8, "application/json")
                            };
                        }

                        // Return object with results and next -> /api/v1/spools?page=2
                        var page1 = new { results = new[] { new { id = 1, name = "First" } }, next = "/api/v1/spools?page=2" };
                        string json1 = JsonSerializer.Serialize(page1);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json1, Encoding.UTF8, "application/json")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        IReadOnlyList<SpoolmanSpoolDto> items = await svc.ListSpoolsAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == 1);
        Assert.Contains(items, i => i.Id == 2);
    }

    [Fact]
    public async Task ListMaterialsAsync_ParsesStringArrayAndObjectArrayFormats()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        // Handler will respond to material endpoint: first call returns string array, second call returns object array
        int call = 0;
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
                {
                    if (req.RequestUri!.AbsolutePath.StartsWith("/api/v1/material"))
                    {
                        call++;
                        if (call == 1)
                        {
                            string[] arr = new[] { "PLA", "ABS" };
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(JsonSerializer.Serialize(arr), Encoding.UTF8, "application/json")
                            };
                        }

                        var objects = new[] { new { id = 10, name = "PETG", density = (double?)1.24 }, new { id = 11, name = "TPU", density = (double?)null } };
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(objects), Encoding.UTF8, "application/json")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        // First call should parse the string array and return two materials
        IReadOnlyList<SpoolmanMaterialDto> mats1 = await svc.ListMaterialsAsync(CancellationToken.None);
        Assert.Equal(2, mats1.Count);
        Assert.Contains(mats1, m => m.Name == "PLA");

        // Second call should parse object array and return object materials
        IReadOnlyList<SpoolmanMaterialDto> mats2 = await svc.ListMaterialsAsync(CancellationToken.None);
        Assert.Contains(mats2, m => m.Name == "PETG" && m.Id == 10);
    }

    [Fact]
    public async Task ListSpoolsAsync_TriesCandidates_AndReturnsItems_WhenOneEndpointSucceeds()
    {
        // configure settings to return base url
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });

        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        // Prepare a message handler that responds to /api/v1/spools with a JSON array of one object
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
                {
                    if (req.RequestUri!.AbsolutePath.StartsWith("/api/v1/spools"))
                    {
                        string json = JsonSerializer.Serialize(new[] { new { id = 42, name = "Test Spool" } });
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

        using HttpClient http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://spoolman.local")
        };

        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        IReadOnlyList<SpoolmanSpoolDto> items = await svc.ListSpoolsAsync(CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(42, items[0].Id);
    }

    [Fact]
    public async Task ScanNetworkForSpoolmanAsync_ReturnsAvailable_WhenIpResponds()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        // Handler that responds to /api/v1/info for one IP and times out for others
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
                {
                    if (req.RequestUri!.Host == "192.168.1.5" && req.RequestUri.AbsolutePath == "/api/v1/info")
                    {
                        string json = JsonSerializer.Serialize(new { version = "1.2.3" });
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

        using HttpClient http = new HttpClient(handler);
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        IEnumerable<SpoolmanDiscoveryResult> results = await svc.ScanNetworkForSpoolmanAsync(new[] { "192.168.1.0/29" });
        // The helper expands small ranges; ensure at least one available result is returned for the 192.168.1.5
        Assert.Contains(results, r => r.IsAvailable && r.Url.Contains("192.168.1.5"));
    }

    [Fact]
    public async Task ListSpoolsAsync_HandlesRelativeNextLinkResolution()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local/root" });
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
            {
                // If the request URI contains page=2 return the second page
                if (req.RequestUri!.AbsoluteUri.Contains("page=2"))
                {
                    string json2 = JsonSerializer.Serialize(new[] { new { id = 102, name = "RelSecond" } });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json2, Encoding.UTF8, "application/json")
                    };
                }

                if (req.RequestUri.AbsolutePath.Contains("/api/v1/spool"))
                {
                    // page 1 has next "/api/v1/spools?page=2" (relative)
                    var page1 = new { results = new[] { new { id = 101, name = "RelFirst" } }, next = "/api/v1/spools?page=2" };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(page1), Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local/root") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        IReadOnlyList<SpoolmanSpoolDto> items = await svc.ListSpoolsAsync(CancellationToken.None);
        Assert.Contains(items, i => i.Id == 101);
        Assert.Contains(items, i => i.Id == 102);
    }

    [Fact]
    public async Task GetSpoolByIdAsync_ReturnsNull_OnNonJsonOrNonSuccess()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
            {
                // If requesting the specific spool, return HTML (non-JSON)
                if (req.RequestUri!.AbsolutePath.EndsWith("/api/v1/spool/99"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html>oops</html>", Encoding.UTF8, "text/html")
                    };
                }

                // For other spools, return 404
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanSpoolDto? res = await svc.GetSpoolByIdAsync(99, CancellationToken.None);
        Assert.Null(res);
    }

    [Fact]
    public async Task ListSpoolsAsync_LogsWarnings_OnEmptySuccessfulResponses()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
        {
            // Return empty successful payload to trigger warning and try next candidate
            string json = JsonSerializer.Serialize(new object[] { });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        IReadOnlyList<SpoolmanSpoolDto> items = await svc.ListSpoolsAsync(CancellationToken.None);
        Assert.Empty(items);
        logger.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("returned 0 spools")), null, null), Times.AtLeastOnce);
    }
}
