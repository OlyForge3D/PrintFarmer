// <copyright file="MoonrakerUploadOutcomeTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerUploadOutcomeTests
{
    [Fact]
    public async Task UploadAndStartPrintAsync_ResponseLostAfterContentSent_ReturnsUnknown()
    {
        using var handler = new ResponseLostAfterContentHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        await using var content = new MemoryStream("G28\n"u8.ToArray());

        UploadAndPrintResult result =
            await ((ISupportsUploadAndPrint)client).UploadAndStartPrintAsync(
                "http://moonraker/",
                "pf-attempt.gcode",
                content,
                ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(UploadAndPrintOutcome.Unknown);
        handler.ContentWasRead.Should().BeTrue(
            "the transport failed only after the start-capable request body was sent");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task UploadAndStartPrintAsync_Explicit4xx_IsFailedBeforeStart(
        HttpStatusCode statusCode)
    {
        using var handler = new InlineHandler(
            _ => new HttpResponseMessage(statusCode));
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        await using var content = new MemoryStream("G28\n"u8.ToArray());

        UploadAndPrintResult result =
            await ((ISupportsUploadAndPrint)client).UploadAndStartPrintAsync(
                "http://moonraker/",
                "rejected.gcode",
                content,
                ct: CancellationToken.None);

        result.Outcome.Should().Be(UploadAndPrintOutcome.FailedBeforeStart);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task UploadAndStartPrintAsync_5xx_IsUnknown(
        HttpStatusCode statusCode)
    {
        using var handler = new InlineHandler(
            _ => new HttpResponseMessage(statusCode));
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        await using var content = new MemoryStream("G28\n"u8.ToArray());

        UploadAndPrintResult result =
            await ((ISupportsUploadAndPrint)client).UploadAndStartPrintAsync(
                "http://moonraker/",
                "uncertain.gcode",
                content,
                ct: CancellationToken.None);

        result.Outcome.Should().Be(UploadAndPrintOutcome.Unknown);
    }

    [Fact]
    public async Task UploadAndStartPrintAsync_UploadPathIsFileIdentity_NotHistoryJobId()
    {
        using var handler = new InlineHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"result":{"item":{"path":"gcodes/pf-attempt.gcode"}}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        await using var content = new MemoryStream("G28\n"u8.ToArray());

        UploadAndPrintResult result =
            await ((ISupportsUploadAndPrint)client).UploadAndStartPrintAsync(
                "http://moonraker/",
                "pf-attempt.gcode",
                content,
                ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        result.BackendJobId.Should().BeNull(
            "Moonraker upload returns a file path, not a history UID");
        result.BackendFileIdentity.Should().Be("gcodes/pf-attempt.gcode");
    }

    [Fact]
    public async Task GetHistoryJobAsync_TrueProviderUid_UsesUidEndpoint()
    {
        using var handler = new InlineHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"result":{"job_id":"uid-123","filename":"pf-attempt.gcode","status":"completed"}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        HistoryJob? history = await client.GetHistoryJobAsync(
            "http://moonraker/",
            "uid-123");

        history.Should().NotBeNull();
        history!.JobId.Should().Be("uid-123");
        handler.LastRequestUri.Should().Contain("server/history/job?uid=uid-123");
    }

    [Fact]
    public async Task GetHistoryJobAsync_Explicit404_ThrowsKeyNotFound()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        Func<Task> action = async () =>
            await client.GetHistoryJobAsync("http://moonraker/", "missing");

        await action.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetHistoryJobAsync_SuccessWithNullResult_ThrowsInvalidData()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"result":null}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        Func<Task> action = async () =>
            await client.GetHistoryJobAsync("http://moonraker/", "provider-job");

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"result":{"count":0}}""")]
    public async Task GetHistoryListAsync_IncompleteSourceEnvelope_IsUnavailable(
        string payload)
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://moonraker/");

        history.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryListAsync_CompleteSourcePayload_IsAuthoritative()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"result":{"count":1,"jobs":[{"job_id":"uid-1","filename":"a.gcode","status":"completed","start_time":1700000000}]}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://moonraker/");

        history.Should().NotBeNull();
        history!.Count.Should().Be(1);
        history.Jobs.Should().ContainSingle();
        history.Jobs[0].JobId.Should().Be("uid-1");
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_MalformedEntry_DoesNotShiftRequestedValidRange()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"result":{"count":3,"jobs":[{"job_id":"bad","filename":"bad.gcode","status":"completed","start_time":"not-a-number"},{"job_id":"first","filename":"first.gcode","status":"completed","start_time":1700000000},{"job_id":"second","filename":"second.gcode","status":"completed","start_time":1700000001}]}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://moonraker/",
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
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"result":{"count":5,"jobs":[null,"malformed",42,{"job_id":"first","filename":"first.gcode","status":"completed","start_time":1700000000},{"job_id":"second","filename":"second.gcode","status":"completed","start_time":1700000001}]}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://moonraker/",
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
    public async Task HistoryList_LowercaseAuxiliaryData_MatchesDetailContract()
    {
        const string job =
            """
            {"job_id":"uid-aux","filename":"aux.gcode","status":"completed","start_time":1700000000,"auxiliary_data":[{"provider":"spoolman","name":"spool_id","value":"42","description":"Physical spool","units":"id"}]}
            """;
        string listPayload = "{\"result\":{\"count\":1,\"jobs\":[" + job + "]}}";
        string detailPayload = "{\"result\":" + job + "}";
        using var handler = new InlineHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith(
                        "/list",
                        StringComparison.Ordinal)
                        ? listPayload
                        : detailPayload,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        HistoryListResponse? list = await client.GetHistoryListAsync(
            "http://moonraker/");
        HistoryJob? detail = await client.GetHistoryJobAsync(
            "http://moonraker/",
            "uid-aux");

        list.Should().NotBeNull();
        detail.Should().NotBeNull();
        AuxiliaryData listAuxiliary = list!.Jobs.Single().AuxiliaryData!.Single();
        AuxiliaryData detailAuxiliary = detail!.AuxiliaryData!.Single();
        listAuxiliary.Provider.Should().Be("spoolman");
        listAuxiliary.Name.Should().Be("spool_id");
        listAuxiliary.Description.Should().Be("Physical spool");
        listAuxiliary.Units.Should().Be("id");
        listAuxiliary.Provider.Should().Be(detailAuxiliary.Provider);
        listAuxiliary.Name.Should().Be(detailAuxiliary.Name);
        listAuxiliary.Value.ToString().Should().Be(
            detailAuxiliary.Value.ToString());
        listAuxiliary.Description.Should().Be(detailAuxiliary.Description);
        listAuxiliary.Units.Should().Be(detailAuxiliary.Units);
    }

    [Fact]
    public async Task GetFileListAsync_InternalTimeout_DegradesToEmptyList()
    {
        // The request never completes, so the method's own CommandTimeout-driven
        // CancelAfter fires internally - not the caller's token. This must degrade to
        // an empty list (the historical bare-catch fallback), not propagate an
        // unhandled OperationCanceledException.
        using var handler = new AsyncMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings
            {
                CommandTimeoutSeconds = 1,
            });

        string[] files = await client.GetFileListAsync(
            "http://moonraker/",
            ct: CancellationToken.None);

        files.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileListAsync_CallerCancellation_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new AsyncMessageHandler((_, _) =>
        {
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(
                new OperationCanceledException(cts.Token));
        });
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());

        Func<Task> action = async () =>
            await client.GetFileListAsync("http://moonraker/", cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class AsyncMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }

    private sealed class ResponseLostAfterContentHandler : HttpMessageHandler
    {
        public bool ContentWasRead { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
            ContentWasRead = true;
            throw new HttpRequestException("Response lost after send.");
        }
    }

    private sealed class InlineHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(responseFactory(request));
        }
    }
}
