// <copyright file="PrusaLinkHistoryAuthorityTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

    [Theory]
    [InlineData("/thumb/job-1.png", "http://prusalink/thumb/job-1.png")]
    [InlineData("thumb/job-1.png", "http://prusalink/thumb/job-1.png")]
    [InlineData("http://prusalink/thumb/absolute.png", "http://prusalink/thumb/absolute.png")]
    [InlineData("https://cdn.example/job-1.png", "https://cdn.example/job-1.png")]
    [InlineData(null, null)]
    public async Task GetHistoryListAsync_ThumbnailReference_IsResolved(
        string? thumbnailReference,
        string? expectedUrl)
    {
        string refs = thumbnailReference is null
            ? string.Empty
            : $",\"refs\":{{\"thumbnail\":{System.Text.Json.JsonSerializer.Serialize(thumbnailReference)}}}";
        string payload =
            "{\"success\":true,\"count\":1,\"results\":[{\"id\":\"job-1\",\"state\":\"completed\",\"startTime\":1700000000,\"job\":{\"file\":{\"name\":\"a.gcode\"" +
            refs +
            "}}}]}";
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                payload));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/");

        history.Should().NotBeNull();
        history!.Jobs.Should().ContainSingle()
            .Which.ThumbnailUrl.Should().Be(expectedUrl);
    }

    [Fact]
    public async Task GetHistoryJobAsync_RelativeThumbnailReference_IsResolved()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryJob? job = await client.GetHistoryJobAsync(
            "http://prusalink/",
            "job-1");

        job!.ThumbnailUrl.Should().Be("http://prusalink/thumb/job-1.png");
    }

    [Fact]
    public async Task GetHistoryJobAsync_CompletedRequest_IsDisposed()
    {
        var requestContent = new DisposalProbeContent();
        using var handler = new InlineHandler(request =>
        {
            request.Content = requestContent;
            return JsonResponse(
                HttpStatusCode.OK,
                """
                {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode"}}}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        await client.GetHistoryJobAsync("http://prusalink/", "job-1");

        requestContent.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryThumbnailAsync_ValidSameOriginImage_ReturnsContent()
    {
        using var handler = new InlineHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/history/job-1"
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """)
                : ImageResponse(
                    HttpStatusCode.OK,
                    "image/png",
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        HistoryThumbnailContent thumbnail =
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        thumbnail.Content.Should().Equal(
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A);
        thumbnail.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task GetHistoryThumbnailAsync_CrossOriginReference_IsRejected()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """
                {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"https://attacker.example/thumb.png"}}}}
                """));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*outside the configured printer endpoint*");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html", typeof(InvalidDataException))]
    [InlineData(HttpStatusCode.BadGateway, "image/png", typeof(HttpRequestException))]
    public async Task GetHistoryThumbnailAsync_InvalidUpstreamResponse_IsRejected(
        HttpStatusCode statusCode,
        string contentType,
        Type expectedException)
    {
        using var handler = new InlineHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/history/job-1"
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """)
                : ImageResponse(statusCode, contentType, [1, 2, 3]));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        (await action.Should().ThrowAsync<Exception>())
            .Which.Should().BeOfType(expectedException);
    }

    [Fact]
    public async Task GetHistoryThumbnailAsync_SpoofedImageContent_IsRejected()
    {
        using var handler = new InlineHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/history/job-1"
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """)
                : ImageResponse(HttpStatusCode.OK, "image/png", [1, 2, 3]));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*valid image*");
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

    [Fact]
    public async Task DeleteHistoryJobAsync_CompletedRequest_IsDisposed()
    {
        var requestContent = new DisposalProbeContent();
        using var handler = new InlineHandler(request =>
        {
            request.Content = requestContent;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        bool deleted = await client.DeleteHistoryJobAsync(
            "http://prusalink/",
            "job-1");

        deleted.Should().BeTrue();
        requestContent.IsDisposed.Should().BeTrue();
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
    public async Task GetHistoryThumbnailAsync_TransportFailure_IsClassifiedAsUpstreamFailure()
    {
        using var handler = new InlineHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/history/job-1"
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """)
                : throw new SocketException((int)SocketError.ConnectionReset));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        (await action.Should().ThrowAsync<HttpRequestException>())
            .WithInnerExceptionExactly<SocketException>();
    }

    [Fact]
    public async Task GetHistoryThumbnailAsync_StreamFailure_IsClassifiedAsUpstreamFailure()
    {
        using var handler = new InlineHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/history/job-1"
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """)
                : throw new IOException("stream aborted"));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        (await action.Should().ThrowAsync<HttpRequestException>())
            .WithInnerExceptionExactly<IOException>();
    }

    [Fact]
    public async Task GetHistoryThumbnailAsync_ClientTimeout_IsClassifiedAsTimeout()
    {
        using var handler = new InlineHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/history/job-1"
                ? JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """)
                : throw new TaskCanceledException(
                    "timed out",
                    new TimeoutException("HttpClient timeout")));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1");

        await action.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task GetHistoryThumbnailAsync_CallerCancellation_StaysCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new InlineHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/history/job-1")
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {"id":"job-1","state":"completed","job":{"file":{"name":"a.gcode","refs":{"thumbnail":"/thumb/job-1.png"}}}}
                    """);
            }

            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        Func<Task> action = async () =>
            await client.GetHistoryThumbnailAsync(
                "http://prusalink/",
                "job-1",
                credentials: null,
                cts.Token);

        (await action.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeOfType<TimeoutException>();
    }

    [Fact]
    public async Task DigestCredentialedRequest_KeepsUsingTheInjectedVettedClient()
    {
        List<HttpRequestMessage> observed = [];
        using var handler = new InlineHandler(request =>
        {
            observed.Add(request);
            if (request.Headers.Authorization is null)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    """realm="Prusalink", nonce="dcd98b7102dd2f0e", qop="auth", algorithm=MD5"""));
                return challenge;
            }

            return JsonResponse(
                HttpStatusCode.OK,
                """{"success":true,"count":0,"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);
        var credentials = new PrinterCredential { Username = "maker", Password = "secret" };

        HistoryListResponse? history = await client.GetHistoryListAsync(
            "http://prusalink/",
            credentials: credentials);

        history.Should().NotBeNull();

        // Both the challenge and the authenticated retry must travel through the injected
        // client's handler. A private HttpClient built around a raw HttpClientHandler would
        // never reach it, silently dropping caller pinning and named-client redirect policies.
        observed.Should().HaveCount(2);
        observed[0].Headers.Authorization.Should().BeNull();
        observed[1].Headers.Authorization.Should().NotBeNull();
        observed[1].Headers.Authorization!.Scheme.Should().Be("Digest");
        observed[1].Headers.Authorization!.Parameter.Should().Contain("nonce=\"dcd98b7102dd2f0e\"");
    }

    [Fact]
    public async Task DigestCredentialedRequest_ReusesCachedChallengeOnSubsequentCalls()
    {
        int challengeCount = 0;
        int authenticatedCount = 0;
        using var handler = new InlineHandler(request =>
        {
            if (request.Headers.Authorization is null)
            {
                challengeCount++;
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Digest",
                    """realm="Prusalink", nonce="dcd98b7102dd2f0e", qop="auth", algorithm=MD5"""));
                return challenge;
            }

            authenticatedCount++;
            return JsonResponse(
                HttpStatusCode.OK,
                """{"success":true,"count":0,"results":[]}""");
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);
        var credentials = new PrinterCredential { Username = "maker", Password = "secret" };

        await client.GetHistoryListAsync("http://prusalink/", credentials: credentials);
        await client.GetHistoryListAsync("http://prusalink/", credentials: credentials);

        // The per-credential transport is cached, so the second call pre-authenticates
        // from the cached challenge instead of paying another 401 round-trip.
        challengeCount.Should().Be(1);
        authenticatedCount.Should().Be(2);
    }

    [Fact]
    public void DigestAuthenticator_RepeatedChallengeNonce_DoesNotReuseNonceCount()
    {
        var authenticator = new DigestAuthenticator("maker", "secret");
        using HttpResponseMessage firstChallenge = DigestChallengeResponse("same-nonce");
        using HttpResponseMessage repeatedChallenge = DigestChallengeResponse("same-nonce");
        using var firstRequest =
            new HttpRequestMessage(HttpMethod.Get, "http://prusalink/api/history");
        using var secondRequest =
            new HttpRequestMessage(HttpMethod.Get, "http://prusalink/api/history");

        authenticator.TryAcceptChallenge(firstChallenge).Should().BeTrue();
        authenticator.ApplyAuthorization(firstRequest);
        authenticator.TryAcceptChallenge(repeatedChallenge).Should().BeTrue();
        authenticator.ApplyAuthorization(secondRequest);

        firstRequest.Headers.Authorization!.Parameter.Should().Contain("nc=00000001");
        secondRequest.Headers.Authorization!.Parameter.Should().Contain("nc=00000002");
    }

    [Fact]
    public async Task PluginRegistration_DirectApiClient_UsesVettedEgressClient()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(
                HttpStatusCode.OK,
                """{"success":true,"count":0,"results":[]}"""));
        using var http = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(http);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<BackendTimeoutSettings>();
        services.AddSingleton<IHttpClientFactory>(factory);
        new PrusaLinkBackendPlugin().RegisterAdditionalServices(services);
        await using ServiceProvider provider = services.BuildServiceProvider();

        IPrusaLinkApiClient client =
            provider.GetRequiredService<IPrusaLinkApiClient>();
        HistoryListResponse? history =
            await client.GetHistoryListAsync("http://prusalink/");

        history.Should().NotBeNull();
        factory.RequestedNames.Should().ContainSingle()
            .Which.Should().Be("VettedEgress");
    }

    [Fact]
    public async Task PluginRegistration_ScopeDisposal_DisposesOwnedHttpClientOnce()
    {
        var handler = new DisposalTrackingHandler();
        var factory = new RecordingHttpClientFactory(new HttpClient(handler));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<BackendTimeoutSettings>();
        services.AddSingleton<IHttpClientFactory>(factory);
        new PrusaLinkBackendPlugin().RegisterAdditionalServices(services);
        ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IPrusaLinkApiClient>();
        }

        handler.DisposeCount.Should().Be(1);
        await provider.DisposeAsync();
        handler.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_BorrowedHttpClient_DoesNotDisposeHandler()
    {
        var handler = new DisposalTrackingHandler();
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(
            http,
            NullLogger<PrusaLinkApiClient>.Instance);

        client.Dispose();
        client.Dispose();

        handler.DisposeCount.Should().Be(0);
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string payload) =>
        new(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage ImageResponse(
        HttpStatusCode status,
        string contentType,
        byte[] payload) =>
        new(status)
        {
            Content = new ByteArrayContent(payload)
            {
                Headers = { ContentType = new(contentType) },
            },
        };

    private static HttpResponseMessage DigestChallengeResponse(string nonce)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
            "Digest",
            $"realm=\"Prusalink\", nonce=\"{nonce}\", qop=\"auth\", algorithm=MD5"));
        return response;
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

    private sealed class InlineHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DisposalProbeContent : HttpContent
    {
        public bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingHttpClientFactory(HttpClient client)
        : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return client;
        }
    }
}
