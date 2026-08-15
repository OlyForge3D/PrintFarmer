// <copyright file="PrusaLinkHistoryAuthorityTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Backends;

public sealed class PrusaLinkHistoryAuthorityTests
{
    [Fact]
    public async Task GetHistoryListAsync_HttpFailure_IsUnavailable()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryListAsync_TransportException_IsUnavailable()
    {
        using var handler = new InlineHandler(_ =>
            throw new HttpRequestException("connection lost"));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().BeNull();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"success":true,"count":0}""")]
    [InlineData("""{"success":true,"results":[]}""")]
    public async Task GetHistoryListAsync_MalformedOrIncompleteEnvelope_IsUnavailable(
        string payload)
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(HttpStatusCode.OK, payload));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryListAsync_ExplicitCompleteEmpty_IsAuthoritative()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """{"success":true,"count":0,"results":[]}"""));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Count.Should().Be(0);
        history.Jobs.Should().BeEmpty();
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_CompleteEntry_IsAuthoritative()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":1,"results":[{"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode"}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle();
        history.Jobs[0].StartTime.Should().Be(1700000000);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_MissingStartTime_DoesNotShiftRequestedValidRange()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":3,"results":[{"id":"bad","state":"completed","job":{"file":{"name":"bad.gcode"}}},{"id":"first","state":"completed","startTime":1700000000,"job":{"file":{"name":"first.gcode"}}},{"id":"second","state":"completed","startTime":1700000001,"job":{"file":{"name":"second.gcode"}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/",
            limit: 1,
            start: 1);

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.JobId.Should().Be("second");
        history.ExaminedSourceEntries.Should().Be(3);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
        history.AuthorityEvidence.ExcludedEntryCount.Should().Be(1);
        history.ExcludedEntries.Should().ContainSingle().Which.Should().Be(
            new HistoryExcludedEntryEvidence(
                "bad",
                "bad.gcode",
                StartTime: null,
                Reason: "malformed_history_entry"));
    }

    [Fact]
    public async Task GetHistoryListAsync_ScalarEntries_AreExcludedWithoutShiftingValidRange()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":5,"results":[null,"malformed",42,{"id":"first","state":"completed","startTime":1700000000,"job":{"file":{"name":"first.gcode"}}},{"id":"second","state":"completed","startTime":1700000001,"job":{"file":{"name":"second.gcode"}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/",
            limit: 1,
            start: 1);

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.JobId.Should().Be("second");
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
                id = $"job-{index:D3}",
                state = "completed",
                startTime = 1700000000 + index,
                job = new { file = new { name = $"job-{index:D3}.gcode" } },
            })
            .ToArray();
        int requestCount = 0;
        using var handler = new InlineHandler(request =>
        {
            requestCount++;
            int start = ReadQueryInt(request, "start");
            int limit = ReadQueryInt(request, "limit");
            return JsonResponse(
                HttpStatusCode.OK,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    success = true,
                    count = entries.Length,
                    results = entries.Skip(start).Take(Math.Min(limit, 100)),
                }));
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/",
            limit: 100,
            start: 0);

        history.Should().NotBeNull();
        history!.Count.Should().Be(250);
        history.Jobs.Should().HaveCount(100);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeFalse();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
        requestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetHistoryJobAsync_Explicit404_ThrowsKeyNotFound()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryJobAsync(
                "http://prusalink/",
                "missing");

        await action.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"success":false}""")]
    [InlineData("""{"success":true}""")]
    public async Task GetHistoryJobAsync_MalformedOrMissingIdentity_ThrowsInvalidData(
        string payload)
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(HttpStatusCode.OK, payload));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryJobAsync(
                "http://prusalink/",
                "provider-job");

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task UploadAndStartPrintAsync_Explicit4xx_IsFailedBeforeStart(
        HttpStatusCode statusCode)
    {
        using var handler = new InlineHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                throw new HttpRequestException("storage probe unavailable");
            }

            return new HttpResponseMessage(statusCode);
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkClient(
            http,
            NullLogger<PrusaLinkClient>.Instance);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("G28\n"));

        UploadAndPrintResult result = await client.UploadAndStartPrintAsync(
            "http://prusalink/",
            "calibration.gcode",
            content);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(UploadAndPrintOutcome.FailedBeforeStart);
    }

    [Fact]
    public async Task GetHistoryListAsync_JobWithRelativeThumbnailRef_ResolvesAbsoluteUrl()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":1,"results":[{"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/api/v1/files/local/a.gcode/thumb"}}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.ThumbnailUrl.Should().Be(
                "http://prusalink/api/v1/files/local/a.gcode/thumb");
    }

    [Fact]
    public async Task GetHistoryListAsync_JobWithAbsoluteThumbnailRef_KeepsUrlUnchanged()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":1,"results":[{"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode","refs":{"thumbnail":"http://elsewhere/a.gcode/thumb"}}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.ThumbnailUrl.Should().Be("http://elsewhere/a.gcode/thumb");
    }

    [Fact]
    public async Task GetHistoryListAsync_JobWithoutThumbnailRef_ThumbnailUrlIsNull()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":1,"results":[{"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode"}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryListAsync_JobWithWhitespaceOnlyThumbnailRef_ThumbnailUrlIsNull()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"count":1,"results":[{"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode","refs":{"thumbnail":"   "}}}}]}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryJobAsync_JobWithRelativeThumbnailRef_ResolvesAbsoluteUrl()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/api/v1/files/local/a.gcode/thumb"}}}}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryJob? job = await client.GetHistoryJobAsync(
            "http://prusalink/",
            "job-1");

        job.Should().NotBeNull();
        job!.ThumbnailUrl.Should().Be(
            "http://prusalink/api/v1/files/local/a.gcode/thumb");
    }

    [Fact]
    public async Task GetHistoryJobAsync_JobWithAbsoluteThumbnailRef_KeepsUrlUnchanged()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode","refs":{"thumbnail":"http://elsewhere/a.gcode/thumb"}}}}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryJob? job = await client.GetHistoryJobAsync(
            "http://prusalink/",
            "job-1");

        job.Should().NotBeNull();
        job!.ThumbnailUrl.Should().Be("http://elsewhere/a.gcode/thumb");
    }

    [Fact]
    public async Task GetHistoryJobAsync_JobWithoutThumbnailRef_ThumbnailUrlIsNull()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"success":true,"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode"}}}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryJob? job = await client.GetHistoryJobAsync(
            "http://prusalink/",
            "job-1");

        job.Should().NotBeNull();
        job!.ThumbnailUrl.Should().BeNull();
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string payload) =>
        new(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    private static int ReadQueryInt(HttpRequestMessage request, string name)
    {
        string value = request.RequestUri!.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Single(parts => string.Equals(parts[0], name, StringComparison.Ordinal))[1];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class InlineHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
