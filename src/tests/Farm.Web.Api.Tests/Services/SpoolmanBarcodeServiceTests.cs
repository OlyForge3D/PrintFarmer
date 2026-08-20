using System.Net;
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

public class SpoolmanBarcodeServiceTests
{
    [Fact]
    public async Task GetFilamentByBarcodeAsync_DuplicateArticleNumbers_ReturnsLowestId()
    {
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 12, name = "Second", article_number = "UPC123", material = "PLA" },
                    new { id = 5, name = "First", article_number = "UPC123", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "2");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("UPC123", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
        Assert.Equal("UPC123", result.ArticleNumber);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_UnknownArticleNumber_ReturnsNull()
    {
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 7, name = "Known", article_number = "OTHER", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("UPC123", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_SpecialCharacters_EncodesOutboundArticleNumber()
    {
        const string barcode = "ABC/DEF 12%3&x=y";
        Uri? requestedUri = null;
        using ServiceHarness harness = CreateService(req =>
        {
            requestedUri = req.RequestUri;
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 9, name = "Special", article_number = barcode, material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync(barcode, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(9, result.Id);
        Assert.Equal(barcode, result.ArticleNumber);
        Assert.NotNull(requestedUri);
        Assert.Contains("article_number=ABC%2FDEF%2012%253%26x%3Dy", requestedUri.Query);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_MatchesNormalizedGtin()
    {
        // Stored gtin "0123456789012" (13-digit) normalizes the same as the scanned 12-digit
        // barcode below: leading zeros never change the GS1 mod-10 check digit.
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 5, name = "Match", gtin = "0123456789012", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_ScannedUpc12ResolvesFilamentStoredAsEan13()
    {
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 11, name = "Ean13Stored", gtin = "0123456789012", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Scan the UPC-12 (GTIN-12) form; the stored value is the equivalent EAN-13 (GTIN-13).
        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(11, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_ScannedEan13ResolvesFilamentStoredAsUpc12()
    {
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 12, name = "Upc12Stored", gtin = "123456789012", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Scan the EAN-13 (GTIN-13) form; the stored value is the equivalent UPC-12 (GTIN-12).
        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("0123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(12, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_LegacyArticleNumberOnly_StillResolves()
    {
        // Record predates the gtin column: gtin is null, only article_number holds the
        // (verbatim, unnormalized) scanned value.
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 20, name = "Legacy", article_number = "123456789012", gtin = (string?)null, material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(20, result.Id);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_ValidRequest_PatchesNormalizedGtin()
    {
        string? patchPayload = null;
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/filament/7")
            {
                return JsonResponse(new { id = 7, name = "Target", article_number = (string?)null, gtin = (string?)null, material = "PLA" });
            }

            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                return JsonResponse(Array.Empty<object>(), totalCount: "0");
            }

            if (req.Method == HttpMethod.Patch && req.RequestUri!.AbsolutePath == "/api/v1/filament/7")
            {
                patchPayload = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new { id = 7, name = "Target", gtin = "00123456789012", material = "PLA" });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.SaveBarcodeMappingAsync(7, "123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("00123456789012", result.Gtin);
        Assert.NotNull(patchPayload);
        using JsonDocument doc = JsonDocument.Parse(patchPayload);
        Assert.Equal("00123456789012", doc.RootElement.GetProperty("gtin").GetString());
        _ = Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_InvalidBarcode_ReturnsNullWithoutPersisting()
    {
        bool anyHttpCallMade = false;
        using ServiceHarness harness = CreateService(_ =>
        {
            anyHttpCallMade = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Bad GS1 mod-10 check digit; must be rejected, never patched to Spoolman.
        SpoolmanFilamentDto? result = await harness.Service.SaveBarcodeMappingAsync(7, "04850807Z", CancellationToken.None);

        Assert.Null(result);
        Assert.False(anyHttpCallMade, "An invalid barcode must be rejected before any Spoolman request is made.");
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_MissingFilament_ReturnsNull()
    {
        using ServiceHarness harness = CreateService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        SpoolmanFilamentDto? result = await harness.Service.SaveBarcodeMappingAsync(404, "123456789012", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_KnownBarcode_PostsResolvedFilamentAndFields()
    {
        string? postPayload = null;
        SpoolmanImportSpoolByBarcodeRequest request = new()
        {
            Barcode = "UPC123",
            RemainingWeight = 955.5,
            InitialWeight = 1000,
            SpoolWeight = 215,
            Location = "Shelf B",
            LotNumber = "LOT-9",
            Price = 29.95,
            Comment = "Mobile import",
        };

        using ServiceHarness harness = CreateService(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 7, name = "Target", article_number = "UPC123", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/v1/spool")
            {
                postPayload = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new
                {
                    id = 88,
                    name = "Imported",
                    material = "PLA",
                    filament_id = 7,
                    remaining_weight = 955.5,
                    initial_weight = 1000,
                    spool_weight = 215,
                    location = "Shelf B",
                    lot_nr = "LOT-9",
                    price = 29.95,
                    comment = "Mobile import",
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanSpoolDto? result = await harness.Service.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(88, result.Id);
        Assert.Equal(7, result.FilamentId);
        Assert.Equal("Shelf B", result.Location);
        Assert.NotNull(postPayload);
        using JsonDocument doc = JsonDocument.Parse(postPayload);
        JsonElement root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("filament_id").GetInt32());
        Assert.Equal(955.5, root.GetProperty("remaining_weight").GetDouble());
        Assert.Equal(1000, root.GetProperty("initial_weight").GetDouble());
        Assert.Equal(215, root.GetProperty("spool_weight").GetDouble());
        Assert.Equal("Shelf B", root.GetProperty("location").GetString());
        Assert.Equal("LOT-9", root.GetProperty("lot_nr").GetString());
        Assert.Equal(29.95, root.GetProperty("price").GetDouble());
        Assert.Equal("Mobile import", root.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_UnknownBarcode_ReturnsNull()
    {
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                return JsonResponse(Array.Empty<object>(), totalCount: "0");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanImportSpoolByBarcodeRequest request = new() { Barcode = "missing" };

        SpoolmanSpoolDto? result = await harness.Service.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        Assert.Null(result);
    }

    private static ServiceHarness CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        Mock<ISettingsService> settings = new();
        _ = settings.Setup(s => s.Get<SpoolmanSettings>()).Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        Mock<ILogger<SpoolmanService>> logger = new();
        FakeHttpMessageHandler handler = new(responder);
        HttpClient http = new(handler) { BaseAddress = new Uri("http://spoolman.local") };
        SpoolmanService service = new(http, settings.Object, logger.Object);
        return new ServiceHarness(service, http, handler);
    }

    private static HttpResponseMessage JsonResponse(object value, string? totalCount = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

        if (totalCount is not null)
        {
            response.Headers.Add("X-Total-Count", totalCount);
        }

        return response;
    }

    private sealed class ServiceHarness(SpoolmanService service, HttpClient http, FakeHttpMessageHandler handler) : IDisposable
    {
        public SpoolmanService Service { get; } = service;

        public void Dispose()
        {
            http.Dispose();
            handler.Dispose();
        }
    }
}
