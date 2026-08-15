using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.OctoPrint;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
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
        (OctoPrintClient? client, _, List<HttpRequestMessage>? _) = CreateClient(req =>
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
        (OctoPrintClient? client, _, List<HttpRequestMessage>? _) = CreateClient(req =>
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
        (OctoPrintClient? client, _, List<HttpRequestMessage>? _) = CreateClient(req =>
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
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "http://octo/api/plugins");
        request.Headers.Add("X-Api-Key", "key");
        // Use reflection to access internal HttpClient property
        PropertyInfo? httpClientProp = typeof(OctoPrintClient).GetProperty("HttpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(httpClientProp);
        object? httpClientObj = httpClientProp.GetValue(client);
        Assert.NotNull(httpClientObj);
        HttpClient httpClient = (HttpClient)httpClientObj!;
        using HttpResponseMessage response = await httpClient.SendAsync(request);
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
    [InlineData("""{"success":true,"results":[]}""")]
    [InlineData("""{"success":true,"count":"one","results":[]}""")]
    public async Task GetHistoryListAsync_IncompleteEnvelope_IsUnavailable(
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
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_MalformedEntry_DoesNotShiftRequestedValidRange()
    {
        (OctoPrintClient client, _, _) = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"success":true,"count":3,"results":[{"name":"bad.gcode","success":true},{"name":"first.gcode","success":true,"timestamp":1700000000},{"name":"second.gcode","success":true,"timestamp":1700000001}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            limit: 1,
            start: 1,
            credential: new PrinterCredential { ApiKey = "key" });

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.JobId.Should().Be("second.gcode");
        history.ExaminedSourceEntries.Should().Be(3);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
        history.AuthorityEvidence.ExcludedEntryCount.Should().Be(1);
        history.ExcludedEntries.Should().ContainSingle().Which.Should().Be(
            new HistoryExcludedEntryEvidence(
                "bad.gcode",
                "bad.gcode",
                StartTime: null,
                Reason: "malformed_history_entry"));
    }

    [Fact]
    public async Task GetHistoryListAsync_ScalarEntries_AreExcludedWithoutShiftingValidRange()
    {
        (OctoPrintClient client, _, _) = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"success":true,"count":5,"results":[null,"malformed",42,{"name":"first.gcode","success":true,"timestamp":1700000000},{"name":"second.gcode","success":true,"timestamp":1700000001}]}""",
                    Encoding.UTF8,
                    "application/json"),
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            limit: 1,
            start: 1,
            credential: new PrinterCredential { ApiKey = "key" });

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.JobId.Should().Be("second.gcode");
        history.ExaminedSourceEntries.Should().Be(5);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
        history.AuthorityEvidence.ExcludedEntryCount.Should().Be(3);
        history.ExcludedEntries.Should().HaveCount(3)
            .And.OnlyContain(entry =>
                entry.BackendJobId == null &&
                entry.Filename == null &&
                entry.StartTime == null);
    }

    [Fact]
    public async Task GetHistoryListAsync_Total250Limit100_ProvesRequestedRange()
    {
        var entries = Enumerable.Range(0, 250)
            .Select(index => new
            {
                name = $"job-{index:D3}.gcode",
                success = true,
                timestamp = 1700000000 + index,
            })
            .ToArray();
        (OctoPrintClient client, _, List<HttpRequestMessage> recorded) =
            CreateClient(request =>
            {
                int start = ReadQueryInt(request, "start");
                int limit = ReadQueryInt(request, "limit");
                return Json(new
                {
                    success = true,
                    count = entries.Length,
                    results = entries.Skip(start).Take(Math.Min(limit, 100)),
                });
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            limit: 100,
            start: 0,
            credential: new PrinterCredential { ApiKey = "key" });

        history.Should().NotBeNull();
        history!.Count.Should().Be(250);
        history.Jobs.Should().HaveCount(100);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeFalse();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
        recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task GetHistoryListAsync_ProviderCap100_Request10000_ReturnsAll1000()
    {
        var entries = Enumerable.Range(0, 1000)
            .Select(index => new
            {
                name = $"seed-{index:D4}.gcode",
                success = true,
                timestamp = 1700000000 + index,
            })
            .ToArray();
        (OctoPrintClient client, _, List<HttpRequestMessage> recorded) =
            CreateClient(request =>
            {
                int start = ReadQueryInt(request, "start");
                int limit = ReadQueryInt(request, "limit");
                return Json(new
                {
                    success = true,
                    count = entries.Length,
                    results = entries.Skip(start).Take(Math.Min(limit, 100)),
                });
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            limit: 10000,
            start: 0,
            credential: new PrinterCredential { ApiKey = "key" });

        history.Should().NotBeNull();
        history!.Jobs.Should().HaveCount(1000);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
        recorded.Should().HaveCount(10);
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_ReturnsNewest50AfterThreeRequests()
    {
        var entries = Enumerable.Range(0, 205)
            .Select(index => new
            {
                name = $"job-{index:D3}.gcode",
                success = true,
                timestamp = 1700000000 + index,
            })
            .ToArray();
        (OctoPrintClient client, _, List<HttpRequestMessage> recorded) =
            CreateClient(request =>
            {
                int start = ReadQueryInt(request, "start");
                int limit = ReadQueryInt(request, "limit");
                return Json(new
                {
                    success = true,
                    count = entries.Length,
                    results = entries.Skip(start).Take(limit),
                });
            });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        recorded.Select(request => request.RequestUri!.PathAndQuery).Should().Equal(
            "/api/history?limit=100&start=0",
            "/api/history?limit=100&start=100",
            "/api/history?limit=100&start=200");
        history.Should().NotBeNull();
        history!.Count.Should().Be(205);
        history.Jobs.Select(job => job.JobId).Should().Equal(
            Enumerable.Range(155, 50)
                .Reverse()
                .Select(index => $"job-{index:D3}.gcode"));
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_AtFullScanBoundUsesTwentyRequests()
    {
        var entries = Enumerable.Range(0, 2000)
            .Select(index => new
            {
                name = $"job-{index:D4}.gcode",
                success = true,
                timestamp = 1700000000 + index,
            })
            .ToArray();
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            int limit = ReadQueryInt(request, "limit");
            return Json(new
            {
                success = true,
                count = entries.Length,
                results = entries.Skip(start).Take(limit),
            });
        });

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        requestCount.Should().Be(20);
        history.Should().NotBeNull();
        history!.Jobs.Should().HaveCount(50);
        history.Jobs[0].JobId.Should().Be("job-1999.gcode");
        history.Jobs[^1].JobId.Should().Be("job-1950.gcode");
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_AboveFullScanBoundFailsAfterOneRequest()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(_ =>
        {
            requestCount++;
            return Json(new
            {
                success = true,
                count = 2001,
                results = new[]
                {
                    new
                    {
                        name = "job-1.gcode",
                        success = true,
                        timestamp = 1700000000,
                    },
                },
            });
        });

        Func<Task> action = async () => await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*authoritative list full-scan limit of 2000*");
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_DriftingCountFailsAfterTwoRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            int count = start == 0 ? 150 : 149;
            int resultCount = Math.Min(100, Math.Max(0, count - start));
            return Json(new
            {
                success = true,
                count,
                results = Enumerable.Range(start, resultCount)
                    .Select(index => new
                    {
                        name = $"job-{index:D3}.gcode",
                        success = true,
                        timestamp = 1700000000 + index,
                    }),
            });
        });

        Func<Task> action = async () => await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*count changed during an authoritative list full scan*");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_NonProgressFailsAfterTwoRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            return Json(new
            {
                success = true,
                count = 101,
                results = start == 0
                    ? Enumerable.Range(0, 100).Select(index => new
                    {
                        name = $"job-{index:D3}.gcode",
                        success = true,
                        timestamp = 1700000000 + index,
                    }).ToArray()
                    : [],
            });
        });

        Func<Task> action = async () => await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*ended before the advertised source count*");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_PageOverrunFailsAfterTwoRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            return Json(new
            {
                success = true,
                count = 150,
                results = Enumerable.Range(start, 100).Select(index => new
                {
                    name = $"job-{index:D3}.gcode",
                    success = true,
                    timestamp = 1700000000 + index,
                }),
            });
        });

        Func<Task> action = async () => await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*count did not cover the returned source page*");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_PageLimitExhaustionFailsAfterTwentyRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            return Json(new
            {
                success = true,
                count = 2000,
                results = Enumerable.Range(start, 99).Select(index => new
                {
                    name = $"job-{index:D4}.gcode",
                    success = true,
                    timestamp = 1700000000 + index,
                }),
            });
        });

        Func<Task> action = async () => await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*full-scan paging limit of 20 pages*");
        requestCount.Should().Be(20);
    }

    [Fact]
    public async Task GetHistoryListAsync_ModalDescendingRequest_ResponseExceedsBodyBound()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(_ =>
        {
            requestCount++;
            HttpResponseMessage response = Json(new
            {
                success = true,
                count = 0,
                results = Array.Empty<object>(),
            });
            response.Content.Headers.ContentLength = (2 * 1024 * 1024) + 1;
            return response;
        });

        Func<Task> action = async () => await client.GetHistoryListAsync(
            "http://octo",
            limit: 50,
            order: "desc",
            credential: new PrinterCredential { ApiKey = "key" });

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*history response exceeded the size limit*");
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_CompletedJobs_ReturnsBoundedTotals()
    {
        (OctoPrintClient client, _, List<HttpRequestMessage> recorded) =
            CreateClient(_ => Json(new
            {
                success = true,
                count = 2,
                results = new[]
                {
                    new
                    {
                        name = "first.gcode",
                        success = true,
                        timestamp = 1700000000,
                        printTime = 120,
                        filament = new { length = 5000 },
                    },
                    new
                    {
                        name = "second.gcode",
                        success = true,
                        timestamp = 1700000100,
                        printTime = 180,
                        filament = new { length = 7000 },
                    },
                },
            }));

        HistoryTotals? totals = await client.GetHistoryTotalsAsync(
            "http://octo",
            new PrinterCredential { ApiKey = "key" });

        recorded.Should().ContainSingle();
        recorded[0].RequestUri!.PathAndQuery.Should().Be(
            "/api/history?limit=100&start=0");
        totals.Should().NotBeNull();
        totals!.JobTotals.TotalJobs.Should().Be(2);
        totals.JobTotals.TotalPrintTime.Should().Be(300);
        totals.JobTotals.TotalFilamentUsed.Should().Be(12);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_MixedStatuses_CountsAllJobsAndAggregatesCompletedJobs()
    {
        (OctoPrintClient client, _, _) = CreateClient(_ => Json(new
        {
            success = true,
            count = 3,
            results = new[]
            {
                new
                {
                    name = "completed-a.gcode",
                    success = true,
                    timestamp = 1700000000,
                    printTime = 120,
                    filament = new { length = 3000 },
                },
                new
                {
                    name = "failed.gcode",
                    success = false,
                    timestamp = 1700000100,
                    printTime = 30,
                    filament = new { length = 500 },
                },
                new
                {
                    name = "completed-b.gcode",
                    success = true,
                    timestamp = 1700000200,
                    printTime = 180,
                    filament = new { length = 7000 },
                },
            },
        }));

        HistoryTotals? totals = await client.GetHistoryTotalsAsync(
            "http://octo");

        totals.Should().NotBeNull();
        totals!.JobTotals.TotalJobs.Should().Be(3);
        totals.JobTotals.TotalPrintTime.Should().Be(300);
        totals.JobTotals.TotalFilamentUsed.Should().Be(10);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_AtAuthorityBound_UsesTwentyRequests()
    {
        var entries = Enumerable.Range(0, 2000)
            .Select(index => new
            {
                name = $"job-{index:D4}.gcode",
                success = index % 2 == 0,
                timestamp = 1700000000 + index,
                printTime = 1,
                filament = new { length = 1000 },
            })
            .ToArray();
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            int limit = ReadQueryInt(request, "limit");
            return Json(new
            {
                success = true,
                count = entries.Length,
                results = entries.Skip(start).Take(limit),
            });
        });

        HistoryTotals? totals = await client.GetHistoryTotalsAsync(
            "http://octo");

        requestCount.Should().Be(20);
        totals.Should().NotBeNull();
        totals!.JobTotals.TotalJobs.Should().Be(2000);
        totals.JobTotals.TotalPrintTime.Should().Be(1000);
        totals.JobTotals.TotalFilamentUsed.Should().Be(1000);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_AboveAuthorityBound_FailsAfterOneRequest()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(_ =>
        {
            requestCount++;
            return Json(new
            {
                success = true,
                count = 2001,
                results = Array.Empty<object>(),
            });
        });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*authoritative totals limit of 2000*");
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_CountDrift_FailsAfterTwoRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            return Json(new
            {
                success = true,
                count = start == 0 ? 101 : 100,
                results = start == 0
                    ? Enumerable.Range(0, 100).Select(index => new
                    {
                        name = $"job-{index:D3}.gcode",
                        success = true,
                        timestamp = 1700000000 + index,
                    }).ToArray()
                    :
                    [
                        new
                        {
                            name = "job-100.gcode",
                            success = true,
                            timestamp = 1700000100,
                        },
                    ],
            });
        });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*count changed while calculating totals*");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_NonProgress_FailsAfterTwoRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            return Json(new
            {
                success = true,
                count = 101,
                results = start == 0
                    ? Enumerable.Range(0, 100).Select(index => new
                    {
                        name = $"job-{index:D3}.gcode",
                        success = true,
                        timestamp = 1700000000 + index,
                    }).ToArray()
                    : [],
            });
        });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*ended before the advertised source count*");
        requestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_PageLimitExhaustion_FailsAfterTwentyRequests()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            return Json(new
            {
                success = true,
                count = 2000,
                results = Enumerable.Range(start, 99).Select(index => new
                {
                    name = $"job-{index:D4}.gcode",
                    success = true,
                    timestamp = 1700000000 + index,
                }),
            });
        });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*paging limit of 20 pages*");
        requestCount.Should().Be(20);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_MalformedEntry_ThrowsInvalidData()
    {
        (OctoPrintClient client, _, _) = CreateClient(_ => Json(new
        {
            success = true,
            count = 1,
            results = new[]
            {
                new
                {
                    name = "missing-timestamp.gcode",
                    success = true,
                },
            },
        }));

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*contained malformed entries*");
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_ResponseExceedsBodyBound_FailsAfterOneRequest()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(_ =>
        {
            requestCount++;
            HttpResponseMessage response = Json(new
            {
                success = true,
                count = 0,
                results = Array.Empty<object>(),
            });
            response.Content.Headers.ContentLength = (2 * 1024 * 1024) + 1;
            return response;
        });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*history response exceeded the size limit*");
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_HttpError_PropagatesAfterOneRequest()
    {
        int requestCount = 0;
        (OctoPrintClient client, _, _) = CreateClient(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        (await action.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_TransportFailure_PropagatesAfterOneRequest()
    {
        int requestCount = 0;
        using var handler = new AsyncMessageHandler((_, _) =>
        {
            requestCount++;
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("backend offline"));
        });
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings());

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<HttpRequestException>();
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_InternalTimeout_PropagatesAsTimeout()
    {
        using var handler = new AsyncMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings
            {
                CommandTimeoutSeconds = 1,
            });

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync("http://octo");

        await action.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task GetHistoryTotalsAsync_CallerCancellation_StaysCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new AsyncMessageHandler((_, _) =>
        {
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(
                new OperationCanceledException(cts.Token));
        });
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings());

        Func<Task> action = async () =>
            await client.GetHistoryTotalsAsync(
                "http://octo",
                credential: null,
                cts.Token);

        (await action.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task UploadAndStartPrintAsync_PostSendIOException_SendsOnceAndReturnsUnknown()
    {
        int requestCount = 0;
        using var handler = new AsyncMessageHandler(async (request, ct) =>
        {
            requestCount++;
            _ = await request.Content!.ReadAsByteArrayAsync(ct);
            throw new HttpRequestException(
                "Connection reset after request body was sent.",
                new IOException("response lost"));
        });
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings());
        await using var content = new MemoryStream([1, 2, 3, 4]);

        UploadAndPrintResult result = await client.UploadAndStartPrintAsync(
            "http://octo",
            "one-shot.gcode",
            content,
            new PrinterCredential { ApiKey = "key" });

        result.Outcome.Should().Be(UploadAndPrintOutcome.Unknown);
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task StartJobAsync_PostSendIOException_SendsExactlyOnce()
    {
        int requestCount = 0;
        using var handler = new AsyncMessageHandler(async (request, ct) =>
        {
            requestCount++;
            _ = await request.Content!.ReadAsByteArrayAsync(ct);
            throw new HttpRequestException(
                "Connection reset after start body was sent.",
                new IOException("response lost"));
        });
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings());

        Func<Task> action = async () => await client.StartJobAsync(
            "http://octo",
            new PrinterCredential { ApiKey = "key" },
            "one-shot.gcode");

        await action.Should().ThrowAsync<HttpRequestException>();
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task UploadFileAsync_TransientRetry_PreservesExactBodyAndContentType()
    {
        var requestBodies = new List<byte[]>();
        var contentTypes = new List<string>();
        using var handler = new AsyncMessageHandler(async (request, ct) =>
        {
            requestBodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            contentTypes.Add(request.Content.Headers.ContentType!.ToString());
            if (requestBodies.Count == 1)
            {
                throw new HttpRequestException(
                    "Connection reset.",
                    new IOException("transient transport failure"));
            }

            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings());
        byte[] fileBytes = [0, 1, 2, 127, 128, 254, 255];

        bool uploaded = await client.UploadFileAsync(
            "http://octo",
            new PrinterCredential { ApiKey = "key" },
            fileBytes,
            "retry.gcode",
            startPrint: false);

        uploaded.Should().BeTrue();
        requestBodies.Should().HaveCount(2);
        requestBodies[1].Should().Equal(requestBodies[0]);
        contentTypes.Should().HaveCount(2);
        contentTypes[1].Should().Be(contentTypes[0]);
        contentTypes[0].Should().StartWith("multipart/form-data; boundary=");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task UploadAndStartPrintAsync_Explicit4xx_IsFailedBeforeStart(
        HttpStatusCode statusCode)
    {
        using var handler = new AsyncMessageHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        using var http = new HttpClient(handler);
        var client = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new Farm.Infrastructure.Settings.BackendTimeoutSettings());
        await using var content = new MemoryStream([1, 2, 3]);

        UploadAndPrintResult result = await client.UploadAndStartPrintAsync(
            "http://octo",
            "rejected.gcode",
            content,
            new PrinterCredential { ApiKey = "key" });

        result.Outcome.Should().Be(UploadAndPrintOutcome.FailedBeforeStart);
    }

    private static int ReadQueryInt(HttpRequestMessage request, string name)
    {
        string value = request.RequestUri!.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Single(parts => string.Equals(parts[0], name, StringComparison.Ordinal))[1];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class AsyncMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await send(request, cancellationToken);
            _responses.Add(response);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (HttpResponseMessage response in _responses)
                {
                    response.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
