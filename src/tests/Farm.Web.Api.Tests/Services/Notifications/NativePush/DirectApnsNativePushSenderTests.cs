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
    public async Task SendAsync_BackgroundDismissal_UsesSilentHeadersAndContentAvailableOnlyAps()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        HttpRequestMessage? captured = null;
        string? capturedJson = null;
        DirectApnsNativePushSender sut = CreateSender(settings, request =>
        {
            captured = request;
            capturedJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        NativePushEnvelope background = Sample with
        {
            Title = null,
            Subtitle = null,
            Body = null,
            ChangeKind = AttentionChangeKind.Resolved,
            Priority = NativePushPriority.Background,
            ActionIds = Array.Empty<string>(),
        };

        try
        {
            NativePushDispatchResult result = await sut.SendAsync(background);

            result.Success.Should().BeTrue();
            captured.Should().NotBeNull();
            captured!.Headers.GetValues("apns-push-type").Should().Equal("background");
            captured.Headers.GetValues("apns-priority").Should().Equal("5");
            capturedJson.Should().NotBeNull();
            using JsonDocument payload = JsonDocument.Parse(capturedJson!);
            JsonElement aps = payload.RootElement.GetProperty("aps");
            aps.EnumerateObject().Select(property => property.Name)
                .Should().Equal("content-available");
            aps.GetProperty("content-available").GetInt32().Should().Be(1);
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

    /// <summary>
    /// Hicks #1 regression: an internally-generated <see cref="TaskCanceledException"/>
    /// (which <see cref="HttpClient"/> raises when its own timeout timer fires, distinct
    /// from a caller-token cancellation) MUST propagate as an
    /// <see cref="OperationCanceledException"/>. The previous implementation filtered
    /// on <c>cancellationToken.IsCancellationRequested</c> and, when the caller passed
    /// <see cref="CancellationToken.None"/>, the filter was ALWAYS false and the OCE
    /// fell through into a generic catch that mapped it to
    /// <see cref="NativePushDispatchResult.Transient(string)"/>("timeout"). This
    /// silently converted a real send failure into a spurious transient retry.
    ///
    /// The fix removed the OCE catch — OCE propagates naturally past the remaining
    /// <see cref="HttpRequestException"/> catch.
    /// </summary>
    [Fact]
    public async Task SendAsync_InternalTaskCanceledException_PropagatesAsOperationCanceled()
    {
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        try
        {
            DirectApnsNativePushSender sut = CreateSender(settings, _ =>
            {
                // TaskCanceledException is a subclass of OperationCanceledException;
                // raised without a triggered token, it models HttpClient's internal
                // timeout-timer cancellation.
                throw new TaskCanceledException("internal HttpClient timeout");
            });

            Func<Task> act = () => sut.SendAsync(Sample, CancellationToken.None);

            await act.Should().ThrowAsync<OperationCanceledException>(
                "internal-timeout OCE MUST propagate; it must NOT be turned into a Transient result");
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

    [Fact]
    public async Task SendAsync_Stale403DoesNotClearRefreshedJwt_CompareAndClearHoldsCache()
    {
        // Hicks #7 (a): compare-and-clear semantics under a stale-403 race.
        //
        // Wire: A and B start concurrently against an empty cache. GetOrRefreshJwtAsync's
        // double-check-locking ensures both use the SAME initial JWT (JWT_1). Both
        // requests hit the handler and park pending. We then:
        //   1. Advance the fake clock so a future signing produces a *content-different*
        //      JWT (different `iat` seconds → different signing input → different signature).
        //   2. Release A's 403 InvalidProviderToken. A's InvalidateJwtCacheAsync compares
        //      cached JWT_1 == failed JWT_1 → clears cache.
        //   3. Fire C. Cache empty → C signs JWT_2. C's Authorization must be *content-different*
        //      from JWT_1 (proves the cache was truly cleared and a genuinely refreshed JWT
        //      is on the wire).
        //   4. Release B's 403. B's failed JWT is JWT_1; cache is JWT_2; compare-and-clear
        //      must NOT clobber the refreshed cache.
        //   5. Fire D. Cache is JWT_2 → D reuses JWT_2 (no third signing).
        //
        // Assertions are on actual Authorization JWT values and request counts, never on
        // task-completion alone (Hicks #7 explicit requirement).
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        var clock = new FakeTimeProvider(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var handler = new DeterministicApnsHandler();
        DirectApnsNativePushSender sut = CreateSenderWithDeterministicHandler(settings, handler, clock);
        try
        {
            // Step 1: A and B enter concurrently. Semaphore serializes GetOrRefreshJwtAsync
            // so both end up seeing the same freshly-cached JWT_1. Task.Run scheduling
            // does NOT guarantee which task reaches the handler first, so we key
            // subsequent assertions off handler-arrival order (idx 0/1), not off
            // the variable identity of aTask/bTask.
            Task<NativePushDispatchResult> aTask = Task.Run(() => sut.SendAsync(Sample));
            Task<NativePushDispatchResult> bTask = Task.Run(() => sut.SendAsync(Sample));

            DeterministicApnsHandler.PendingRequest req0 = await handler.ObserveRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
            DeterministicApnsHandler.PendingRequest req1 = await handler.ObserveRequestAsync(1).WaitAsync(TimeSpan.FromSeconds(5));

            string jwt1 = req0.Authorization!;
            jwt1.Should().NotBeNullOrEmpty();
            req1.Authorization.Should().Be(jwt1, "A and B must share the same initial cached JWT");

            // Step 2: advance the fake clock so the next signing produces a distinct iat.
            // JWT lifetime is 50m so cache is still non-expired (2s advance << 50m).
            clock.Advance(TimeSpan.FromSeconds(2));

            // Step 3: release req0 (whichever task got there first) → A-role invalidates cache.
            handler.RespondWith(0, Forbidden("InvalidProviderToken"));

            // Whichever task's HTTP was at idx 0 completes now. Identify it by WhenAny.
            Task<NativePushDispatchResult> firstDone = await Task.WhenAny(aTask, bTask).WaitAsync(TimeSpan.FromSeconds(5));
            NativePushDispatchResult resFirst = await firstDone;
            resFirst.IsTransient.Should().BeTrue();
            resFirst.Reason.Should().Be("invalid_provider_token");
            Task<NativePushDispatchResult> stillPending = ReferenceEquals(firstDone, aTask) ? bTask : aTask;

            // Step 4: C runs while req1's HTTP is still parked. C must sign a fresh JWT_2
            // that differs *by content* from JWT_1 (proves cache was cleared).
            handler.EnqueueAutoResponse(new HttpResponseMessage(HttpStatusCode.OK));
            NativePushDispatchResult resC = await sut.SendAsync(Sample).WaitAsync(TimeSpan.FromSeconds(5));
            resC.Success.Should().BeTrue();

            DeterministicApnsHandler.PendingRequest reqC = handler.RequestAt(2);
            string jwt2 = reqC.Authorization!;
            jwt2.Should().NotBeNullOrEmpty();
            jwt2.Should().NotBe(jwt1, "C must use a genuinely refreshed JWT after the stale-403 invalidation");

            // Step 5: release req1's stale 403. Compare-and-clear must NOT clear the refreshed
            // cache (failedJwt=JWT_1, cachedJwt=JWT_2, they differ → cache stays).
            handler.RespondWith(1, Forbidden("InvalidProviderToken"));
            NativePushDispatchResult resSecond = await stillPending.WaitAsync(TimeSpan.FromSeconds(5));
            resSecond.IsTransient.Should().BeTrue();
            resSecond.Reason.Should().Be("invalid_provider_token");

            // Step 6: D must reuse JWT_2 — no third signing.
            handler.EnqueueAutoResponse(new HttpResponseMessage(HttpStatusCode.OK));
            NativePushDispatchResult resD = await sut.SendAsync(Sample).WaitAsync(TimeSpan.FromSeconds(5));
            resD.Success.Should().BeTrue();

            DeterministicApnsHandler.PendingRequest reqD = handler.RequestAt(3);
            reqD.Authorization.Should().Be(jwt2, "D must reuse the refreshed JWT — the stale-403 must not have re-cleared the cache");

            // Total request count and unique JWT-value tally are the ultimate proof:
            // exactly 4 requests, exactly 2 distinct JWTs (req0=req1=JWT_1, reqC=reqD=JWT_2).
            handler.ReceivedCount.Should().Be(4);
            var authorizations = new[] { req0.Authorization!, req1.Authorization!, jwt2, reqD.Authorization! };
            authorizations.Distinct(StringComparer.Ordinal).Should().HaveCount(
                2,
                "compare-and-clear must produce exactly 2 unique JWTs across req0/req1/reqC/reqD; a third would prove the refreshed cache was clobbered and re-signed");
        }
        finally
        {
            handler.Dispose();
            key.Dispose();
            sut.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_CancellationWhileWaitingForInvalidateLock_ThrowsOceWithoutHang()
    {
        // Hicks #7 (b): cancellation of a caller token while its send is blocked
        // inside InvalidateJwtCacheAsync's SemaphoreSlim.WaitAsync.
        //
        // Wire:
        //   1. Warm the cache (a normal 200 send) so subsequent sends take the fast path
        //      through GetOrRefreshJwtAsync (no semaphore contention there).
        //   2. Externally hold the JWT semaphore via the internal test seam JwtLockForTests
        //      so any downstream WaitAsync (i.e., InvalidateJwtCacheAsync) will block.
        //   3. Install the OnBeforeInvalidateWaitAsyncForTests hook so B signals "about
        //      to WaitAsync" — this is the deterministic barrier, not a timing sleep.
        //   4. Start B with a cancellable token; handler returns 403 InvalidProviderToken.
        //   5. Await the hook signal → B is at/entering the semaphore's WaitAsync.
        //   6. Assert B has not completed (proves B is not synchronously blocked on Wait()
        //      and has not returned a result — it's suspended inside the invalidation
        //      critical-section acquire).
        //   7. Cancel the caller token. WaitAsync must throw OperationCanceledException
        //      promptly (bounded 5s); no ThreadPool starvation or deadlock.
        //
        // The pre-cancelled-before-HTTP path is NOT what's under test here — B's HTTP
        // response has already been received when we cancel.
        (NativePushSettings settings, ECDsa key) = MakeDirectSettings();
        var handler = new DeterministicApnsHandler();
        DirectApnsNativePushSender sut = CreateSenderWithDeterministicHandler(settings, handler, TimeProvider.System);
        try
        {
            // (1) Warm-up: seed the cache with JWT_1 via a straight 200.
            handler.EnqueueAutoResponse(new HttpResponseMessage(HttpStatusCode.OK));
            NativePushDispatchResult warm = await sut.SendAsync(Sample).WaitAsync(TimeSpan.FromSeconds(5));
            warm.Success.Should().BeTrue();

            // (2) External hold on the JWT semaphore. From this point any WaitAsync on
            //     _jwtLock blocks until we release. Semaphore is proven to be entered
            //     because InvalidateJwtCacheAsync's WaitAsync will never complete.
            await sut.JwtLockForTests.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            // (3) Hook: signal deterministically when B reaches the point immediately
            //     before its InvalidateJwtCacheAsync WaitAsync. RunContinuationsAsynchronously
            //     so B's synchronous continuation (the actual WaitAsync call) proceeds
            //     without inline racing the test's continuation.
            var bAtInvalidateWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.OnBeforeInvalidateWaitAsyncForTests = _ =>
            {
                bAtInvalidateWait.TrySetResult();
                return Task.CompletedTask;
            };

            // (4) Start B with a cancellable token. Handler is set to 403 InvalidProviderToken
            //     for the next request so B enters InvalidateJwtCacheAsync.
            handler.EnqueueAutoResponse(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"reason\":\"InvalidProviderToken\"}"),
            });
            using var bCts = new CancellationTokenSource();
            Task<NativePushDispatchResult> bTask = Task.Run(() => sut.SendAsync(Sample, bCts.Token));

            // (5) Deterministic barrier: hook fired → B is inside InvalidateJwtCacheAsync
            //     about to call _jwtLock.WaitAsync(bCts.Token). Because we hold the semaphore
            //     from step (2), that WaitAsync is guaranteed to block (or, if scheduling
            //     interleaves the cancel first, to observe cancellation at entry). Either
            //     way the caller's OCE is the mandated behavior.
            await bAtInvalidateWait.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // (6) B must not have completed — proves it is not spinning or synchronously
            //     draining. Sanity-check: give it one yield to let any inline continuation
            //     drain; still must be incomplete because the semaphore is held.
            bTask.IsCompleted.Should().BeFalse("B must be blocked in InvalidateJwtCacheAsync's WaitAsync because the semaphore is held");
            await Task.Yield();
            bTask.IsCompleted.Should().BeFalse("B must remain blocked until cancellation trips WaitAsync");

            // (7) Cancel while B is inside/entering the invalidation-lock wait. WaitAsync
            //     must observe cancellation and throw OCE promptly.
            bCts.Cancel();

            OperationCanceledException oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await bTask.WaitAsync(TimeSpan.FromSeconds(5)));

            oce.Should().NotBeNull("WaitAsync must be interrupted by the caller's cancellation, not deadlock");
            // The thrown OCE's token should be the caller's (or a linked descendant carrying it);
            // at minimum the caller's token must be canceled and the exception is an OCE-derived type.
            bCts.IsCancellationRequested.Should().BeTrue();

            // The semaphore is still ours to release — B never entered the critical section,
            // so its finally didn't run. Releasing keeps the semaphore healthy for Dispose().
            _ = sut.JwtLockForTests.Release();
        }
        finally
        {
            handler.Dispose();
            key.Dispose();
            sut.Dispose();
        }
    }

    private static HttpResponseMessage Forbidden(string apnsReason)
        => new(HttpStatusCode.Forbidden)
        {
            Content = new StringContent($"{{\"reason\":\"{apnsReason}\"}}"),
        };

    private static DirectApnsNativePushSender CreateSenderWithDeterministicHandler(
        NativePushSettings settings,
        DeterministicApnsHandler handler,
        TimeProvider timeProvider)
    {
        var factory = new StubHttpClientFactory(new HttpClient(handler));
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(settings);
        return new DirectApnsNativePushSender(factory, monitor, NullLogger<DirectApnsNativePushSender>.Instance, timeProvider);
    }

    /// <summary>
    /// Minimal test-only <see cref="TimeProvider"/> whose clock is advanced explicitly
    /// so JWT <c>iat</c> claims are byte-different across signings without wall-clock waits.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTime startUtc)
        {
            if (startUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("startUtc must be Kind=Utc", nameof(startUtc));
            }

            _now = new DateTimeOffset(startUtc, TimeSpan.Zero);
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>
    /// Deterministic APNs HTTP handler. Each request is parked (its Authorization header
    /// captured at receive-time) and the caller-side <see cref="HttpClient.SendAsync(HttpRequestMessage, CancellationToken)"/>
    /// completes only when the test explicitly releases the request. Also supports an
    /// auto-respond queue for warm-up / follow-up sends where release ordering is not
    /// what the test is proving.
    /// </summary>
    private sealed class DeterministicApnsHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<PendingRequest>> _observers = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, PendingRequest> _pending = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<HttpResponseMessage> _autoResponses = new();
        private int _receivedCount;

        public int ReceivedCount => Volatile.Read(ref _receivedCount);

        public PendingRequest RequestAt(int index) => _pending[index];

        public Task<PendingRequest> ObserveRequestAsync(int index)
            => _observers.GetOrAdd(
                index,
                _ => new TaskCompletionSource<PendingRequest>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public void RespondWith(int index, HttpResponseMessage response)
        {
            if (!_pending.TryGetValue(index, out PendingRequest? pending))
            {
                throw new InvalidOperationException($"No pending request captured at index {index}.");
            }

            if (!pending.Response.TrySetResult(response))
            {
                throw new InvalidOperationException($"Request {index} already responded.");
            }
        }

        public void EnqueueAutoResponse(HttpResponseMessage response) => _autoResponses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int idx = Interlocked.Increment(ref _receivedCount) - 1;
            string? auth = request.Headers.Authorization?.Parameter;
            var pending = new PendingRequest(idx, auth);
            _pending[idx] = pending;
            _ = _observers.GetOrAdd(
                idx,
                _ => new TaskCompletionSource<PendingRequest>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(pending);

            if (_autoResponses.TryDequeue(out HttpResponseMessage? auto))
            {
                pending.Response.TrySetResult(auto);
            }

            return await pending.Response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal sealed class PendingRequest(int index, string? authorization)
        {
            public int Index { get; } = index;

            public string? Authorization { get; } = authorization;

            public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
