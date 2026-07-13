using System.Net;
using System.Net.Http;
using System.Text.Json;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications.NativePush;

/// <summary>
/// Verifies the relay sender's HTTP contract: bearer auth, JSON envelope shape, and the
/// 2xx / 410 / 4xx / 5xx / network response translation matrix.
/// </summary>
public sealed class RelayNativePushSenderTests
{
    private static readonly NativePushEnvelope Sample = new(
        DeviceTokenId: Guid.NewGuid().ToString("D"),
        Token: "device-token-abc",
        Platform: "ios",
        Environment: "production",
        AppBundleId: "com.example.app",
        Category: AttentionPushCategories.PrinterFailure,
        ThreadId: "printer:x:failure",
        Title: "Printer A",
        Subtitle: null,
        Body: "Print failed",
        AttentionItemId: "att-1",
        AttentionKind: AttentionKind.Failure,
        ChangeKind: AttentionChangeKind.Created,
        PrinterId: Guid.NewGuid(),
        JobId: null,
        ToolheadIndex: null,
        DeepLink: "printfarmer://attention/att-1",
        Priority: NativePushPriority.Alert,
        ExpiresAtUtc: null,
        ActionIds: new[] { AttentionPushCategories.ActionPause });

    [Fact]
    public async Task SendAsync_MissingEndpointOrApiKey_ReturnsNotConfigured()
    {
        var settings = new NativePushSettings { Mode = NativePushMode.Relay };
        RelayNativePushSender sut = CreateSender(settings, out _);

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be("notConfigured");
    }

    [Fact]
    public async Task SendAsync_Http2xx_ReturnsDelivered_AndSendsBearerAuth()
    {
        var settings = MakeRelaySettings();
        Uri? capturedUri = null;
        string? capturedScheme = null;
        string? capturedParam = null;
        string? capturedBody = null;
        RelayNativePushSender sut = CreateSender(settings, out _, req =>
        {
            capturedUri = req.RequestUri;
            capturedScheme = req.Headers.Authorization?.Scheme;
            capturedParam = req.Headers.Authorization?.Parameter;
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.Success.Should().BeTrue();
        capturedUri!.ToString().Should().Be("https://relay.example.com/v1/apns");
        capturedScheme.Should().Be("Bearer");
        capturedParam.Should().Be("secret-key");
        using JsonDocument doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("token").GetString().Should().Be("device-token-abc");
        doc.RootElement.GetProperty("category").GetString().Should().Be("PRINTER_FAILURE");
        doc.RootElement.GetProperty("deepLink").GetString().Should().Be("printfarmer://attention/att-1");
    }

    [Fact]
    public async Task SendAsync_Http410_ReturnsInvalidated()
    {
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            new HttpResponseMessage(HttpStatusCode.Gone));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.TokenInvalidated.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_Http4xx_ReturnsTerminal()
    {
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.Success.Should().BeFalse();
        result.IsTransient.Should().BeFalse();
        result.TokenInvalidated.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_Http5xx_ReturnsTransient()
    {
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_Http404_ReturnsTerminalButRetainsToken()
    {
        // Regression: 404 is NOT part of the APNs invalidation contract. A relay that
        // wants a token deleted must send 410. Treating 404 as invalidation caused
        // legitimate tokens to be hard-deleted on relay routing mistakes.
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.Success.Should().BeFalse();
        result.IsTransient.Should().BeFalse();
        result.TokenInvalidated.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_Http408_ReturnsTransient()
    {
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            new HttpResponseMessage(HttpStatusCode.RequestTimeout));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.IsTransient.Should().BeTrue();
        result.TokenInvalidated.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_Http429_ReturnsTransient()
    {
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            new HttpResponseMessage((HttpStatusCode)429));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.IsTransient.Should().BeTrue();
        result.TokenInvalidated.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_NetworkException_ReturnsTransient()
    {
        RelayNativePushSender sut = CreateSender(MakeRelaySettings(), out _, _ =>
            throw new HttpRequestException("boom"));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.IsTransient.Should().BeTrue();
        result.Reason.Should().Be("network");
    }

    private static NativePushSettings MakeRelaySettings() => new()
    {
        Mode = NativePushMode.Relay,
        Relay = new NativePushRelaySettings
        {
            Endpoint = "https://relay.example.com/v1/apns",
            ApiKey = "secret-key",
            InstallationId = "install-1",
        },
    };

    private static RelayNativePushSender CreateSender(
        NativePushSettings settings,
        out StubHttpClientFactory factory,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new StubHandler(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        factory = new StubHttpClientFactory(new HttpClient(handler) { BaseAddress = null });
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(settings);
        return new RelayNativePushSender(factory, monitor, NullLogger<RelayNativePushSender>.Instance);
    }

    private sealed class StaticOptionsMonitor(NativePushSettings value) : IOptionsMonitor<NativePushSettings>
    {
        public NativePushSettings CurrentValue { get; } = value;

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
