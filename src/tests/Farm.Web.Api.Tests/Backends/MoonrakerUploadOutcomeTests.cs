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
