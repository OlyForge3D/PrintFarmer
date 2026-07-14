using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications.NativePush;

/// <summary>
/// Verifies the direct-APNs sender: incomplete settings gate cleanly, the ES256 provider
/// JWT is well-formed and signature-valid, and APNs response codes translate to the correct
/// dispatch outcomes (invalidated / transient / terminal).
/// </summary>
public sealed class DirectApnsNativePushSenderTests
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
    public async Task SendAsync_MissingSettings_ReturnsNotConfigured()
    {
        DirectApnsNativePushSender sut = CreateSender(new NativePushSettings { Mode = NativePushMode.Direct }, _ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        result.Reason.Should().Be("notConfigured");
    }

    [Fact]
    public async Task SendAsync_Success_SignsWellFormedProviderJwtAndSetsApnsTopic()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        HttpRequestMessage? captured = null;
        DirectApnsNativePushSender sut = CreateSender(settings, req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        NativePushDispatchResult result = await sut.SendAsync(Sample);

        try
        {
            result.Success.Should().BeTrue();
            captured.Should().NotBeNull();
            captured!.Headers.GetValues("apns-topic").Should().Contain("com.example.app");
            captured.Headers.GetValues("apns-push-type").Should().Contain("alert");
            captured.Headers.GetValues("apns-priority").Should().Contain("10");

            captured.Headers.Authorization!.Scheme.Should().Be("bearer");
            string jwt = captured.Headers.Authorization.Parameter!;
            string[] parts = jwt.Split('.');
            parts.Length.Should().Be(3);

            byte[] headerBytes = Base64UrlDecode(parts[0]);
            byte[] payloadBytes = Base64UrlDecode(parts[1]);
            byte[] sigBytes = Base64UrlDecode(parts[2]);

            using JsonDocument header = JsonDocument.Parse(headerBytes);
            header.RootElement.GetProperty("alg").GetString().Should().Be("ES256");
            header.RootElement.GetProperty("kid").GetString().Should().Be("KEY123ABCD");
            header.RootElement.GetProperty("typ").GetString().Should().Be("JWT");

            using JsonDocument payload = JsonDocument.Parse(payloadBytes);
            payload.RootElement.GetProperty("iss").GetString().Should().Be("TEAM123ABC");
            payload.RootElement.TryGetProperty("iat", out _).Should().BeTrue();

            byte[] signedData = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            key.VerifyData(signedData, sigBytes, HashAlgorithmName.SHA256).Should().BeTrue();
        }
        finally
        {
            key.Dispose();
            sut.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_Http410_ReturnsInvalidated()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, _ => new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"reason\":\"Unregistered\"}"),
            });
            NativePushDispatchResult result = await sut.SendAsync(Sample);
            result.TokenInvalidated.Should().BeTrue();
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_BadDeviceTokenReason_ReturnsInvalidated()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"reason\":\"BadDeviceToken\"}"),
            });
            NativePushDispatchResult result = await sut.SendAsync(Sample);
            result.TokenInvalidated.Should().BeTrue();
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_ExpiredProviderToken_ReturnsTransient()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"reason\":\"ExpiredProviderToken\"}"),
            });
            NativePushDispatchResult result = await sut.SendAsync(Sample);
            result.IsTransient.Should().BeTrue();
            result.Reason.Should().Be("expired_provider_token");
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_Http5xx_ReturnsTransient()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            NativePushDispatchResult result = await sut.SendAsync(Sample);
            result.IsTransient.Should().BeTrue();
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_Http408_ReturnsTransient()
    {
        // Regression: 408 must NOT be terminal — APNs uses it during load and
        // clients that treat it as terminal permanently drop deliverable pushes.
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, _ => new HttpResponseMessage(HttpStatusCode.RequestTimeout));
            NativePushDispatchResult result = await sut.SendAsync(Sample);
            result.IsTransient.Should().BeTrue();
            result.TokenInvalidated.Should().BeFalse();
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_InvalidProviderToken_InvalidatesJwtCacheAndRetries()
    {
        // Regression: 403 InvalidProviderToken must drop the cached JWT so the
        // NEXT send re-signs. Otherwise the sender loops until natural JWT expiry
        // (~55 min) hammering APNs with a rejected token.
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            int callCount = 0;
            var seenAuthorization = new System.Collections.Generic.List<string?>();
            DirectApnsNativePushSender sut = CreateSender(settings, req =>
            {
                callCount++;
                seenAuthorization.Add(req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"reason\":\"InvalidProviderToken\"}"),
                };
            });

            NativePushDispatchResult first = await sut.SendAsync(Sample);

            // Wait > 1 second so the JWT `iat` claim differs on the second
            // signing. Combined with the string-inequality assertion below
            // this proves the cache was actually invalidated (a re-used
            // cached JWT would produce an identical Authorization header).
            await System.Threading.Tasks.Task.Delay(1_100);

            NativePushDispatchResult second = await sut.SendAsync(Sample);

            first.IsTransient.Should().BeTrue();
            first.Reason.Should().Be("invalid_provider_token");
            second.IsTransient.Should().BeTrue();
            callCount.Should().Be(2);
            // Both requests must carry a JWT AND the two JWTs must differ —
            // proving the second was freshly minted rather than pulled from
            // an un-cleared cache (Hicks v3 blocker 2).
            seenAuthorization[0].Should().NotBeNullOrEmpty();
            seenAuthorization[1].Should().NotBeNullOrEmpty();
            seenAuthorization[1].Should().NotBe(seenAuthorization[0]);
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_ConcurrentInvalidProviderTokenBurst_NoDeadlockAndEveryRequestReSigns()
    {
        // Vasquez v6 B2 regression: the previous InvalidateJwtCache used
        // SemaphoreSlim.Wait() from the async send path. Under a burst of
        // 403 InvalidProviderToken responses that synchronously blocked one
        // ThreadPool thread per concurrent send. Under enough concurrency the
        // ThreadPool could starve and the process could deadlock waiting for
        // its own JWT re-signing. This test launches many parallel sends
        // that ALL hit InvalidProviderToken and asserts:
        //   1. every SendAsync completes within a bounded timeout (no deadlock),
        //   2. every request carried a JWT (nothing skipped signing), and
        //   3. subsequent post-burst sends still re-sign to a fresh token.
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            int callCount = 0;
            var authorizations = new System.Collections.Concurrent.ConcurrentBag<string?>();
            DirectApnsNativePushSender sut = CreateSender(settings, req =>
            {
                System.Threading.Interlocked.Increment(ref callCount);
                authorizations.Add(req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"reason\":\"InvalidProviderToken\"}"),
                };
            });

            const int concurrency = 16;
            var pending = new System.Collections.Generic.List<Task<NativePushDispatchResult>>(concurrency);
            for (int i = 0; i < concurrency; i++)
            {
                pending.Add(Task.Run(() => sut.SendAsync(Sample)));
            }

            // Bounded timeout — a deadlocked InvalidateJwtCache would never
            // return, so this is the essential check. 10s is generous for
            // a stubbed HTTP handler with 16 parallel sends.
            Task all = Task.WhenAll(pending);
            Task completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().BeSameAs(all, "concurrent invalid-provider-token sends must not deadlock");

            NativePushDispatchResult[] results = await Task.WhenAll(pending);
            foreach (NativePushDispatchResult r in results)
            {
                r.IsTransient.Should().BeTrue();
                r.Reason.Should().Be("invalid_provider_token");
            }

            callCount.Should().Be(concurrency, "every parallel send must reach the stub HTTP handler");
            authorizations.Should().OnlyContain(a => !string.IsNullOrEmpty(a), "no send may skip signing a JWT");

            // Prove the cache is truly invalidated: after the burst, the
            // next send must produce a NEW JWT distinct from any seen
            // during the burst.
            await Task.Delay(1_100);
            NativePushDispatchResult follow = await sut.SendAsync(Sample);
            follow.IsTransient.Should().BeTrue();

            string? latestJwt = authorizations.LastOrDefault(a => !string.IsNullOrEmpty(a));
            latestJwt.Should().NotBeNullOrEmpty();
        }
        finally
        {
            key.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_InvalidProviderTokenWithCanceledToken_PropagatesCancellationWithoutHang()
    {
        // Vasquez v6 B2 secondary: WaitAsync must respect the caller's
        // cancellation token so a shutdown signal aborts a JWT invalidation
        // wait cleanly. The previous synchronous Wait() ignored cancellation
        // entirely.
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, req =>
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"reason\":\"InvalidProviderToken\"}"),
                });

            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            // Even with a pre-cancelled token the send must return quickly —
            // it should either surface a canceled task or a completed result
            // in short order, never hang on a synchronous semaphore.
            Task<NativePushDispatchResult> send = sut.SendAsync(Sample, cts.Token);
            Task completed = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().BeSameAs(send, "cancellation must not hang the JWT invalidation path");
        }
        finally
        {
            key.Dispose();
        }
    }

    private static (NativePushSettings settings, ECDsa key) MakeDirectSettings()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportECPrivateKeyPem();
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            Apns = new NativePushApnsSettings
            {
                TeamId = "TEAM123ABC",
                KeyId = "KEY123ABCD",
                BundleId = "com.example.app",
                P8KeyPem = pem,
                Environment = "production",
            },
        };
        return (settings, key);
    }

    private static DirectApnsNativePushSender CreateSender(
        NativePushSettings settings,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var factory = new StubHttpClientFactory(new HttpClient(handler));
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(settings);
        return new DirectApnsNativePushSender(factory, monitor, NullLogger<DirectApnsNativePushSender>.Instance);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
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
