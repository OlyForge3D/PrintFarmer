using System.Net;
using System.Net.Http.Headers;
using Farm.Backend.Plugin.Core;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class BackendHttpTransportTests
{
    private const string Url = "http://printer/api/ping";
    private const string BeforeEvent = "before";
    private const string SendEvent = "send";
    private const string Digest = "Digest";
    private const string Username = "maker";
    private const string Password = "secret";

    [Fact]
    public async Task SendAsync_WithoutCredentials_ReportsTheSingleAttemptBeforeSendingIt()
    {
        List<string> events = [];
        using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK), events);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = new(http);
        using HttpRequestMessage request = new(HttpMethod.Get, Url);

        using HttpResponseMessage response = await transport.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, () => events.Add(BeforeEvent), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([BeforeEvent, SendEvent], events);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(Username, null)]
    [InlineData(null, Password)]
    [InlineData(" ", Password)]
    [InlineData(Username, " ")]
    public async Task SendAsync_IncompleteCredentials_DoesNotRetryAChallengeAndReportsOneAttempt(
        string? username, string? password)
    {
        List<string> events = [];
        using StubHandler handler = new(_ => Challenge(), events);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = new(http, username, password);
        using HttpRequestMessage request = new(HttpMethod.Get, Url);

        using HttpResponseMessage response = await transport.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, () => events.Add(BeforeEvent), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal([BeforeEvent, SendEvent], events);
    }

    [Fact]
    public async Task SendAsync_DigestChallenge_ReportsTheRetryAsItsOwnAttemptAndKeepsTheRange()
    {
        List<string> events = [];
        using StubHandler handler = new(DigestServer, events);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = new(http, Username, Password);
        using HttpRequestMessage request = new(HttpMethod.Get, Url);
        request.Headers.Range = new RangeHeaderValue(0, 9);

        using HttpResponseMessage response = await transport.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, () => events.Add(BeforeEvent), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([BeforeEvent, SendEvent, BeforeEvent, SendEvent], events);
        Assert.Equal([null, Digest], handler.Authorizations);
        Assert.Equal(["bytes=0-9", "bytes=0-9"], handler.Ranges);
    }

    [Fact]
    public async Task SendAsync_CachedChallenge_PreAuthenticatesLaterRequestsSoTheyCostOneAttempt()
    {
        List<string> events = [];
        using StubHandler handler = new(DigestServer, events);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = new(http, Username, Password);

        using (HttpRequestMessage first = new(HttpMethod.Get, Url))
        using (await transport.SendAsync(
            first, HttpCompletionOption.ResponseHeadersRead, () => events.Add(BeforeEvent), CancellationToken.None))
        {
            Assert.Equal([BeforeEvent, SendEvent, BeforeEvent, SendEvent], events);
        }

        events.Clear();
        using HttpRequestMessage second = new(HttpMethod.Get, Url);
        using HttpResponseMessage response = await transport.SendAsync(
            second, HttpCompletionOption.ResponseHeadersRead, () => events.Add(BeforeEvent), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([BeforeEvent, SendEvent], events);
        Assert.Equal([null, Digest, Digest], handler.Authorizations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_BeforeSendThrowsOnTheFirstAttempt_NothingIsSent(bool digest)
    {
        using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK), []);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = digest ? new(http, Username, Password) : new(http);
        using HttpRequestMessage request = new(HttpMethod.Get, Url);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                () => throw new InvalidOperationException("budget"),
                CancellationToken.None));

        Assert.Equal("budget", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendAsync_BeforeSendThrowsOnTheDigestRetry_TheRetryIsNeverSent()
    {
        List<string> events = [];
        using StubHandler handler = new(DigestServer, events);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = new(http, Username, Password);
        using HttpRequestMessage request = new(HttpMethod.Get, Url);
        int attempts = 0;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                () =>
                {
                    attempts++;
                    if (attempts == 2)
                    {
                        throw new InvalidOperationException("budget");
                    }
                },
                CancellationToken.None));

        Assert.Equal("budget", error.Message);
        Assert.Equal(2, attempts);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SendAsync_ExistingOverloads_StillWorkWithoutACallback()
    {
        using StubHandler handler = new(DigestServer, []);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = new(http, Username, Password);

        using (HttpRequestMessage buffered = new(HttpMethod.Get, Url))
        using (HttpResponseMessage response = await transport.SendAsync(buffered, CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpRequestMessage streamed = new(HttpMethod.Get, Url);
        using HttpResponseMessage streamedResponse = await transport.SendAsync(
            streamed, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, streamedResponse.StatusCode);
        Assert.Equal([null, Digest, Digest], handler.Authorizations);
    }

    [Theory]
    [InlineData(HttpCompletionOption.ResponseContentRead, false, true)]
    [InlineData(HttpCompletionOption.ResponseHeadersRead, false, false)]
    [InlineData(HttpCompletionOption.ResponseContentRead, true, true)]
    [InlineData(HttpCompletionOption.ResponseHeadersRead, true, false)]
    public async Task SendAsync_CompletionOption_AppliesToEveryWireSend(
        HttpCompletionOption option, bool digest, bool expectedBuffered)
    {
        TrackingContent? content = null;
        using StubHandler handler = new(
            request =>
            {
                if (digest && request.Headers.Authorization is null)
                {
                    return Challenge();
                }

                content = new TrackingContent();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            },
            []);
        using HttpClient http = new(handler, disposeHandler: false);
        BackendHttpTransport transport = digest ? new(http, Username, Password) : new(http);
        using HttpRequestMessage request = new(HttpMethod.Get, Url);

        using HttpResponseMessage response = await transport.SendAsync(
            request, option, beforeSend: null, CancellationToken.None);

        Assert.Equal(digest ? 2 : 1, handler.Calls);
        Assert.NotNull(content);
        Assert.Equal(expectedBuffered, content.Serialized);
    }

    private static HttpResponseMessage Challenge()
    {
        HttpResponseMessage response = new(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd($"{Digest} realm=\"printer\", nonce=\"first\", qop=\"auth\"");
        return response;
    }

    private static HttpResponseMessage DigestServer(HttpRequestMessage request) =>
        request.Headers.Authorization?.Scheme == Digest ? new HttpResponseMessage(HttpStatusCode.OK) : Challenge();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, List<string> events)
        : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        public List<string?> Ranges { get; } = [];

        public int Calls => Authorizations.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            events.Add(SendEvent);
            Authorizations.Add(request.Headers.Authorization?.Scheme);
            Ranges.Add(request.Headers.Range?.ToString());
            return Task.FromResult(respond(request));
        }
    }

    // HttpClient only serializes response content into memory when the caller asked for buffering.
    private sealed class TrackingContent : HttpContent
    {
        private static readonly byte[] Payload = [1, 2, 3];

        public bool Serialized { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Serialized = true;
            return stream.WriteAsync(Payload).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Payload.Length;
            return true;
        }
    }
}
