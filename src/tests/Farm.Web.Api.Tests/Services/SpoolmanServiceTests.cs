using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Farm.Web.Api.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
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
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(() => (SpoolmanSettings?)null);
#pragma warning restore CS8603 // Possible null reference return

        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();
        using FakeHttpMessageHandler _handler = new FakeHttpMessageHandler();
        using HttpClient http = new HttpClient(_handler);

        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanPagedResult<SpoolmanSpoolDto> result = await svc.ListSpoolsAsync(new SpoolmanSpoolQueryParams(), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ListSpoolsAsync_ReturnsItemsWithTotalCount()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
                {
                    if (req.RequestUri!.AbsolutePath.StartsWith("/api/v1/spool"))
                    {
                        string json = JsonSerializer.Serialize(new[] { new { id = 1, name = "First" }, new { id = 2, name = "Second" } });
                        HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                        response.Headers.Add("X-Total-Count", "42");
                        return response;
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanPagedResult<SpoolmanSpoolDto> result = await svc.ListSpoolsAsync(new SpoolmanSpoolQueryParams { Limit = 50 }, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(42, result.TotalCount);
        Assert.Contains(result.Items, i => i.Id == 1);
        Assert.Contains(result.Items, i => i.Id == 2);
    }

    [Fact]
    public async Task ListMaterialsAsync_ParsesStringArrayAndObjectArrayFormats()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

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
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });

        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

        // Prepare a message handler that responds to /api/v1/spool/ (correct singular endpoint)
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
                {
                    if (req.RequestUri!.AbsolutePath.StartsWith("/api/v1/spool"))
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

        SpoolmanPagedResult<SpoolmanSpoolDto> result = await svc.ListSpoolsAsync(new SpoolmanSpoolQueryParams(), CancellationToken.None);

        _ = Assert.Single(result.Items);
        Assert.Equal(42, result.Items[0].Id);
    }

    [Fact]
    public async Task ScanNetworkForSpoolmanAsync_ReturnsAvailable_WhenIpResponds()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

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
    public async Task ListSpoolsAsync_PassesQueryParamsToSpoolman()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

        string? capturedUrl = null;
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
            {
                capturedUrl = req.RequestUri!.AbsoluteUri;
                string json = JsonSerializer.Serialize(new[] { new { id = 101, name = "Filtered" } });
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                response.Headers.Add("X-Total-Count", "1");
                return response;
            });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanPagedResult<SpoolmanSpoolDto> result = await svc.ListSpoolsAsync(
            new SpoolmanSpoolQueryParams
            {
                Limit = 25,
                Offset = 50,
                Sort = "filament.name:asc",
                Search = "PLA",
                Material = "PLA",
                Location = "Shelf A",
                AllowArchived = true,
            },
            CancellationToken.None);

        Assert.NotNull(capturedUrl);
        Assert.Contains("limit=25", capturedUrl);
        Assert.Contains("offset=50", capturedUrl);
        Assert.Contains("sort=", capturedUrl);
        Assert.Contains("filament.name=", capturedUrl);
        Assert.Contains("filament.material=", capturedUrl);
        Assert.Contains("location=", capturedUrl);
        Assert.Contains("allow_archived=true", capturedUrl);
        Assert.Single(result.Items);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetSpoolByIdAsync_ReturnsNull_OnNonJsonOrNonSuccess()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

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
    public async Task ListSpoolsAsync_ReturnsEmpty_WhenEndpointReturnsEmptyArray()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

        FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
        {
            // Return empty successful payload
            string json = JsonSerializer.Serialize(new object[] { });
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-Total-Count", "0");
            return response;
        });

        HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanPagedResult<SpoolmanSpoolDto> result = await svc.ListSpoolsAsync(new SpoolmanSpoolQueryParams(), CancellationToken.None);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    // -------------------------------------------------------------------------
    // BuildFilamentQueryUrl tests
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildFilamentQueryUrl_WithNoParams_ReturnsBaseUrl()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl("http://spoolman.local", new SpoolmanFilamentQueryParams());
        Assert.Equal("http://spoolman.local/api/v1/filament", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithLimitAndOffset_AppendsQueryString()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Limit = 25, Offset = 50 });

        Assert.Contains("limit=25", url);
        Assert.Contains("offset=50", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithZeroOffset_OmitsOffsetParam()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Limit = 10, Offset = 0 });

        Assert.DoesNotContain("offset", url);
        Assert.Contains("limit=10", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithSearch_AppendsNameParam()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Search = "PLA" });

        Assert.Contains("name=PLA", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithMaterial_AppendsMaterialParam()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Material = "PETG" });

        Assert.Contains("material=PETG", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithVendor_AppendsVendorNameParam()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Vendor = "Polymaker" });

        Assert.Contains("vendor.name=Polymaker", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithSort_AppendsSortParam()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Sort = "name:asc" });

        Assert.Contains("sort=name%3Aasc", url);
    }

    [Fact]
    public void BuildFilamentQueryUrl_WithSearchContainingSpaces_UrlEncodesParam()
    {
        string url = SpoolmanService.BuildFilamentQueryUrl(
            "http://spoolman.local",
            new SpoolmanFilamentQueryParams { Search = "PolyTerra PLA" });

        Assert.Contains("name=PolyTerra%20PLA", url);
    }

    [Fact]
    public async Task ListFilamentsPagedAsync_ReturnsEmpty_WhenNotConfigured()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
#pragma warning disable CS8603
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(() => (SpoolmanSettings?)null);
#pragma warning restore CS8603

        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();
        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler();
        using HttpClient http = new HttpClient(handler);

        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanPagedResult<SpoolmanFilamentDto> result =
            await svc.ListFilamentsPagedAsync(new SpoolmanFilamentQueryParams(), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ListFilamentsPagedAsync_ReturnsItemsWithTotalCount()
    {
        Mock<ISettingsService> settings = new Mock<ISettingsService>();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new Mock<ILogger<SpoolmanService>>();

        using FakeHttpMessageHandler handler = new FakeHttpMessageHandler((req) =>
        {
            if (req.RequestUri!.AbsolutePath.StartsWith("/api/v1/filament"))
            {
                string json = JsonSerializer.Serialize(new[]
                {
                    new { id = 1, name = "PolyTerra PLA", material = "PLA" },
                    new { id = 2, name = "PolyTerra PETG", material = "PETG" },
                });
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                response.Headers.Add("X-Total-Count", "100");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using HttpClient http = new HttpClient(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService svc = new SpoolmanService(http, settings.Object, logger.Object);

        SpoolmanPagedResult<SpoolmanFilamentDto> result =
            await svc.ListFilamentsPagedAsync(new SpoolmanFilamentQueryParams { Limit = 50 }, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(100, result.TotalCount);
        Assert.Contains(result.Items, i => i.Id == 1);
        Assert.Contains(result.Items, i => i.Id == 2);
    }
}
