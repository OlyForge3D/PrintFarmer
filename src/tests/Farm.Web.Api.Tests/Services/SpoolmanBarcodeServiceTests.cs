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
    public async Task GetFilamentByBarcodeAsync_DuplicateGtins_ReturnsLowestId()
    {
        // `gtin` is intentionally non-unique (multipacks and vendor parent listings
        // legitimately share one), so duplicates must resolve deterministically.
        const string storedGtin = "00123456789012";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 12, name = "Second", gtin = storedGtin, material = "PLA" },
                    new { id = 5, name = "First", gtin = storedGtin, material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "2");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
        Assert.Equal(storedGtin, result.Gtin);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_UnknownBarcode_ReturnsNull()
    {
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 7, name = "Known", gtin = "00012345678905", material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_InvalidBarcode_ReturnsNullWithoutQueryingSpoolman()
    {
        bool anyHttpCallMade = false;
        using ServiceHarness harness = CreateService(_ =>
        {
            anyHttpCallMade = true;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Not a GTIN in any accepted form; there is no `article_number` path left to try, so
        // resolution must reject it outright rather than issuing a lookup.
        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("ABC/DEF 12%3&x=y", CancellationToken.None);

        Assert.Null(result);
        Assert.False(anyHttpCallMade, "An unnormalizable barcode must be rejected before any Spoolman request is made.");
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_NeverQueriesSpoolmanByArticleNumber()
    {
        List<Uri> requestedUris = [];
        using ServiceHarness harness = CreateService(req =>
        {
            requestedUris.Add(req.RequestUri!);
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                // Nothing matches, forcing every resolution stage (filtered then full scan)
                // to run so the assertion below covers all outbound lookups.
                return JsonResponse(Array.Empty<object>(), totalCount: "0");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.Null(result);
        Assert.NotEmpty(requestedUris);
        Assert.All(requestedUris, uri => Assert.Null(GetQueryParam(uri, "article_number")));
        Assert.Contains(requestedUris, uri => GetQueryParam(uri, "gtin") == "00123456789012");
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_ServerRejectsGtinFilter_RetriesUnfilteredAndResolves()
    {
        // An older Spoolman that does not know the `gtin=` query param answers the filtered
        // request with an error (NOT an empty result set). Resolution must drop the filter and
        // retry with a full scan, so a farm running a lagging Spoolman still resolves scans.
        const string storedGtin = "00123456789012";
        List<bool> requestsHadGtinParam = [];
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                bool hasGtinParam = GetQueryParam(req, "gtin") is not null;
                requestsHadGtinParam.Add(hasGtinParam);
                object[] filaments = [new { id = 31, name = "LaggingServerMatch", gtin = storedGtin, material = "PLA" }];

                return hasGtinParam
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(31, result.Id);
        Assert.Equal([true, false], requestsHadGtinParam);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_UnfilteredScanFails_DoesNotReissueIdenticalRequest()
    {
        // The filter-drop retry exists solely to recover from a server rejecting the `gtin=`
        // param. The full-scan stage sends no filter, so there is nothing left to drop --
        // retrying would reissue a byte-identical request against an already-failing endpoint.
        int unfilteredRequestCount = 0;
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                if (GetQueryParam(req, "gtin") is not null)
                {
                    // Filtered stage succeeds but matches nothing, so the full scan runs next.
                    return JsonResponse(Array.Empty<object>(), totalCount: "0");
                }

                unfilteredRequestCount++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, unfilteredRequestCount);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_StoredGtinAlreadyNormalized_MatchesViaServerSideFilterDirectly()
    {
        // Stored gtin is already the normalized 14-digit form (as written by
        // SaveBarcodeMappingAsync), so the server-side exact-match `gtin=` filter finds it
        // directly -- this must succeed without ever needing the unfiltered full-scan
        // fallback exercised by the tests above.
        const string storedGtin = "00123456789012";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                string? gtinParam = GetQueryParam(req, "gtin");
                object[] filaments = [new { id = 5, name = "Match", gtin = storedGtin, material = "PLA" }];

                return gtinParam == storedGtin
                    ? JsonResponse(filaments, totalCount: "1")
                    : JsonResponse(Array.Empty<object>(), totalCount: "0");
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
        // Stored gtin "0123456789012" (13-digit, not zero-padded to 14) is logically the same
        // product as the scanned UPC-12 below, but a real Spoolman server-side `gtin=` filter
        // does an EXACT STRING match: the normalized 14-digit search value
        // ("00123456789012") does not equal the stored 13-digit value. Resolution now queries
        // every equivalent zero-pad literal (12/13/14-digit forms) via the `gtin=` filter, so
        // this must resolve directly via the 13-digit literal candidate -- not the unfiltered
        // full scan (which this handler still supports, as a defensive check that it is not
        // needed here: it would return zero rows if reached).
        const string storedGtin = "0123456789012";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments = [new { id = 11, name = "Ean13Stored", gtin = storedGtin, material = "PLA" }];
                string? gtinParam = GetQueryParam(req, "gtin");

                if (gtinParam is not null)
                {
                    return gtinParam == storedGtin
                        ? JsonResponse(filaments, totalCount: "1")
                        : JsonResponse(Array.Empty<object>(), totalCount: "0");
                }

                // Unfiltered full scan: must NOT be needed for this test -- returning zero rows
                // here would surface as a test failure if resolution ever regressed to relying
                // on it instead of the targeted literal-candidate queries.
                return JsonResponse(Array.Empty<object>(), totalCount: "0");
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
        // Mirror of the test above: stored gtin "123456789012" (12-digit) is logically the
        // same product as the scanned EAN-13 below. Resolution now queries every equivalent
        // zero-pad literal via the `gtin=` filter, so this must resolve directly via the
        // 12-digit literal candidate -- not the unfiltered full scan.
        const string storedGtin = "123456789012";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments = [new { id = 12, name = "Upc12Stored", gtin = storedGtin, material = "PLA" }];
                string? gtinParam = GetQueryParam(req, "gtin");

                if (gtinParam is not null)
                {
                    return gtinParam == storedGtin
                        ? JsonResponse(filaments, totalCount: "1")
                        : JsonResponse(Array.Empty<object>(), totalCount: "0");
                }

                // Unfiltered full scan: must NOT be needed for this test -- see comment above.
                return JsonResponse(Array.Empty<object>(), totalCount: "0");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Scan the EAN-13 (GTIN-13) form; the stored value is the equivalent UPC-12 (GTIN-12).
        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("0123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(12, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_ArticleNumberOnlyWithoutGtin_IsNotResolved()
    {
        // `article_number` means a vendor article number / SKU. A filament reachable only
        // through it must NOT be resolved by a barcode scan: matching a scanned barcode
        // against a SKU field is a category error and risks colliding with numeric SKUs.
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments =
                [
                    new { id = 20, name = "SkuOnly", article_number = "123456789012", gtin = (string?)null, material = "PLA" },
                ];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_StoredGtinWithSeparators_ResolvesViaFullScanFallback()
    {
        // A stored gtin formatted with separators (e.g. dashes) is not a plain zero-pad literal,
        // so none of the exact-match literal candidates queried by
        // CollectMatchesForEquivalentGtinLiteralsAsync can match it server-side. This is the one
        // case that genuinely requires the unfiltered full-scan fallback (client-side isMatch
        // normalizes both sides, stripping the separators).
        const string storedGtin = "0123-4567-8901-2";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                object[] filaments = [new { id = 30, name = "SeparatorFormatted", gtin = storedGtin, material = "PLA" }];
                string? gtinParam = GetQueryParam(req, "gtin");

                if (gtinParam is not null)
                {
                    // No literal candidate (plain digit strings) ever equals the separator-
                    // formatted stored value under an exact string match.
                    return JsonResponse(Array.Empty<object>(), totalCount: "0");
                }

                // Unfiltered full scan: the fallback this test exercises.
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(30, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_MixedFormatDuplicateGtins_LowestIdWinsAcrossFormats()
    {
        // Regression for #1872: two filaments share the same logical GTIN but were written in
        // different literal formats. The lowest-ID filament (5) stores the CANONICAL 14-digit
        // form, which GetEquivalentGtinLiterals queries LAST (candidate order is 12/13/14-digit
        // for this GTIN's significant-digit length) -- while the higher-ID filament (20) stores
        // the UPC-12 form, queried FIRST. Placing the winner on the later-queried candidate
        // proves resolution merges every literal candidate's results before the lowest-ID
        // tie-break runs, rather than short-circuiting on whichever literal query returns a
        // match first.
        const string upc12Gtin = "123456789012";
        const string canonicalGtin = "00123456789012";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                string? gtinParam = GetQueryParam(req, "gtin");
                return gtinParam switch
                {
                    upc12Gtin => JsonResponse(
                        new object[] { new { id = 20, name = "Upc12Duplicate", gtin = upc12Gtin, material = "PLA" } },
                        totalCount: "1"),
                    canonicalGtin => JsonResponse(
                        new object[] { new { id = 5, name = "CanonicalDuplicate", gtin = canonicalGtin, material = "PLA" } },
                        totalCount: "1"),
                    _ => JsonResponse(Array.Empty<object>(), totalCount: "0"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync(canonicalGtin, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_MixedFormatDuplicateGtins_LowestIdWinsRegardlessOfScanFormat()
    {
        // Mirror of the test above, scanning the UPC-12 form instead of the canonical form.
        // Regardless of which equivalent literal is scanned, normalization must recognize both
        // stored records as the same GTIN, merge both candidates, and the deterministic
        // lowest-ID selection must hold -- id 5 (the later-queried canonical-form duplicate)
        // still wins over id 20 (the first-queried UPC-12-form duplicate).
        const string upc12Gtin = "123456789012";
        const string canonicalGtin = "00123456789012";
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                string? gtinParam = GetQueryParam(req, "gtin");
                return gtinParam switch
                {
                    upc12Gtin => JsonResponse(
                        new object[] { new { id = 20, name = "Upc12Duplicate", gtin = upc12Gtin, material = "PLA" } },
                        totalCount: "1"),
                    canonicalGtin => JsonResponse(
                        new object[] { new { id = 5, name = "CanonicalDuplicate", gtin = canonicalGtin, material = "PLA" } },
                        totalCount: "1"),
                    _ => JsonResponse(Array.Empty<object>(), totalCount: "0"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync(upc12Gtin, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_GtinFilterRejected_PerformsSingleFullScanNotOnePerLiteral()
    {
        // Regression: if Spoolman rejects/errors on the `gtin=` filter, resolution must stop
        // after the first rejection and perform exactly ONE unfiltered full scan, not retry a
        // full scan independently for each of the (up to four) equivalent literal candidates.
        // Falling through to a full scan per candidate would multiply one barcode lookup into
        // several full-table scans on a Spoolman instance that doesn't support the filter.
        int filteredRequestCount = 0;
        int fullScanRequestCount = 0;
        using ServiceHarness harness = CreateService(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/filament")
            {
                string? gtinParam = GetQueryParam(req, "gtin");
                if (gtinParam is not null)
                {
                    filteredRequestCount++;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                fullScanRequestCount++;
                object[] filaments = [new { id = 5, name = "Match", gtin = "123456789012", material = "PLA" }];
                return JsonResponse(filaments, totalCount: "1");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        SpoolmanFilamentDto? result = await harness.Service.GetFilamentByBarcodeAsync("00123456789012", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
        Assert.Equal(1, filteredRequestCount);
        Assert.Equal(1, fullScanRequestCount);
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
            Barcode = "123456789012",
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
                    new { id = 7, name = "Target", gtin = "00123456789012", material = "PLA" },
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

        // Valid GTIN so resolution actually queries Spoolman (which matches nothing) rather
        // than short-circuiting on normalization.
        SpoolmanImportSpoolByBarcodeRequest request = new() { Barcode = "123456789012" };

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
        SpoolmanService service = new(http, settings.Object, logger.Object, Farm.Testing.Shared.AppDbTestHelpers.PermissiveEgressGuard());
        return new ServiceHarness(service, http, handler);
    }

    /// <summary>
    /// Reads a single query string parameter's raw (unescaped) value from a request URI, or
    /// null if absent. Used by fake handlers to simulate Spoolman's real exact-string-match
    /// filtering behavior instead of ignoring the query string.
    /// </summary>
    private static string? GetQueryParam(HttpRequestMessage req, string name) =>
        GetQueryParam(req.RequestUri!, name);

    private static string? GetQueryParam(Uri uri, string name)
    {
        string query = uri.Query.TrimStart('?');
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == name)
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
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
