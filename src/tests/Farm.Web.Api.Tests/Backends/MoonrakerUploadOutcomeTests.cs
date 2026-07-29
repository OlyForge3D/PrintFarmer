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

namespace Farm.Web.Api.Tests.Backends;

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
    public async Task GetHistoryListAsync_MalformedEntry_IsExcludedWithoutVoidingAuthority()
    {
        using var handler = new InlineHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"result":{"count":2,"jobs":[{"job_id":"bad","filename":"bad.gcode","status":"completed","start_time":"not-a-number"},{"job_id":"good","filename":"good.gcode","status":"completed","start_time":1700000000}]}}
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
            limit: 2,
            start: 0);

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.JobId.Should().Be("good");
        history.ExaminedSourceEntries.Should().Be(2);
        history.AuthorityEvidence!.ProvesCompleteSource.Should().BeTrue();
        history.AuthorityEvidence.ProvesRequestedRange.Should().BeTrue();
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
