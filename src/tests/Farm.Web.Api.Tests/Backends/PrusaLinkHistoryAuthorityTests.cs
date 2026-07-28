// <copyright file="PrusaLinkHistoryAuthorityTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Text;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
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
    [InlineData("""{"success":true,"count":2,"results":[{"id":"job-1","state":"completed","startTime":1700000000,"job":{"file":{"name":"a.gcode"}}}]}""")]
    [InlineData("""{"success":true,"count":1,"results":[{}]}""")]
    [InlineData("""{"success":true,"count":1,"results":[{"id":"job-1"}]}""")]
    [InlineData("""{"success":true,"count":1,"results":[{"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode"}}}]}""")]
    public async Task GetHistoryListAsync_MalformedOrIncompletePayload_IsUnavailable(
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
    public async Task GetHistoryListAsync_FullRequestedPage_IsPaginationAmbiguous()
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
            "http://prusalink/",
            limit: 1,
            start: 0);

        history.Should().NotBeNull();
        history!.AuthorityEvidence!.ProvesCompleteSource.Should().BeFalse();
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

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string payload) =>
        new(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

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
