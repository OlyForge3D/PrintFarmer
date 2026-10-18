// <copyright file="WebhookServiceDeliveryTests.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Webhooks;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.Webhooks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Webhooks;

/// <summary>
/// Drives the real <see cref="WebhookService"/> drain loop against a loopback HTTP receiver
/// (issue #3344). Subscriptions use public TEST-NET-3 addresses so the production
/// private/reserved-address check runs unchanged; the test <see cref="IHttpClientFactory"/>
/// only redirects the TCP connect to loopback, so a real <see cref="HttpClient"/> timeout,
/// connection refusal, and HTTP status handling are exercised end to end.
/// </summary>
public sealed class WebhookServiceDeliveryTests : IAsyncLifetime, IDisposable
{
    private const string PublicHost = "203.0.113.10";
    private const int RefusedUrlPort = 81;
    private const string Secret = "s3cr3t-signing-key";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-tests-{Guid.NewGuid():N}.db");
    private readonly List<RecordedMeasurement> _measurements = new();
    private readonly object _measurementsLock = new();
    private readonly PoisonDeliveryLogInterceptor _poison = new();

    private DbContextOptions<AppDbContext> _dbOptions = null!;
    private ServiceProvider _serviceProvider = null!;
    private LoopbackReceiver _receiver = null!;
    private WebhookMetrics _metrics = null!;
    private MeterListener _meterListener = null!;
    private RedirectingHttpClientFactory? _httpClientFactory;
    private WebhookService? _sut;

    public async Task InitializeAsync()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False;Default Timeout=30")
            .AddInterceptors(_poison)
            .Options;

        await using (var db = new AppDbContext(_dbOptions))
        {
            await db.Database.EnsureCreatedAsync();
        }

        ServiceCollection services = new();
        services.AddScoped(_ => new AppDbContext(_dbOptions));
        _serviceProvider = services.BuildServiceProvider();

        _receiver = new LoopbackReceiver();
        _metrics = new WebhookMetrics();

        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            // Filter by meter instance so a parallel test's WebhookMetrics cannot leak in.
            if (ReferenceEquals(instrument.Meter, _metrics.Meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<int>((i, v, tags, _) => Record(i, v, tags));
        _meterListener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _meterListener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _meterListener.Start();
    }

    public async Task DisposeAsync()
    {
        if (_sut is not null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _sut.StopAsync(stopCts.Token);
        }

        await _receiver.DisposeAsync();
        _sut?.Dispose();
        _sut = null;
        await _serviceProvider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    public void Dispose()
    {
        _meterListener.Dispose();
        _metrics.Dispose();
        _httpClientFactory?.Dispose();
        _sut?.Dispose();
        _serviceProvider?.Dispose();
    }

    [Fact(DisplayName = "HttpClient timeout is a failed attempt: logged, retried, counted, and later subscribers still receive the event")]
    public async Task Timeout_IsRetriedAndCounted_AndDoesNotSkipLaterSubscribers()
    {
        _receiver.Respond("/slow", _ => new ResponsePlan(200, TimeSpan.FromSeconds(10)));
        _receiver.Respond("/fast", _ => new ResponsePlan(200, TimeSpan.Zero));

        // Inserted first so the hanging subscriber is iterated before the healthy one.
        Guid slow = await AddSubscriptionAsync("/slow");
        Guid fast = await AddSubscriptionAsync("/fast");

        await StartAsync(httpTimeout: TimeSpan.FromMilliseconds(300), retryDelay: TimeSpan.FromMilliseconds(50));
        _sut!.Enqueue("job.completed", new { jobId = 1 });

        await WaitUntilAsync(async () => (await GetSubscriptionAsync(slow)).ConsecutiveFailures == 1);

        List<WebhookDeliveryLog> slowLogs = await GetLogsAsync(slow);
        slowLogs.Select(l => l.Attempt).Should().Equal(1, 2, 3);
        slowLogs.Should().OnlyContain(l => !l.Success && l.StatusCode == null);
        slowLogs.Should().OnlyContain(l => l.ErrorMessage!.Contains("HttpClient.Timeout"));
        slowLogs.Should().OnlyContain(l => l.DurationMs >= 250);
        _receiver.RequestsFor("/slow").Should().HaveCount(3);

        WebhookSubscription fastSub = await GetSubscriptionAsync(fast);
        _receiver.RequestsFor("/fast").Should().ContainSingle();
        fastSub.LastSuccessAt.Should().NotBeNull();
        fastSub.ConsecutiveFailures.Should().Be(0);

        Measurements("webhook.attempt_duration_ms")
            .Where(m => m.Tag(WebhookMetrics.OutcomeTag) == WebhookMetrics.OutcomeTimeout)
            .Should().HaveCount(3)
            .And.OnlyContain(m => m.Value >= 250);
        Sum("webhook.deliveries_exhausted").Should().Be(1);
    }

    [Fact(DisplayName = "Refused connections are retried, logged and counted toward consecutive failures")]
    public async Task ConnectionRefused_IsRetriedAndCounted()
    {
        Guid refused = await AddSubscriptionAsync("/refused", port: RefusedUrlPort);

        await StartAsync(retryDelay: TimeSpan.FromMilliseconds(50));
        _sut!.Enqueue("job.failed", new { jobId = 2 });

        await WaitUntilAsync(async () => (await GetSubscriptionAsync(refused)).ConsecutiveFailures == 1);

        List<WebhookDeliveryLog> logs = await GetLogsAsync(refused);
        logs.Select(l => l.Attempt).Should().Equal(1, 2, 3);
        logs.Should().OnlyContain(l => !l.Success && l.StatusCode == null && !string.IsNullOrEmpty(l.ErrorMessage));
        _receiver.AllRequests.Should().BeEmpty();
        Measurements("webhook.attempt_duration_ms")
            .Select(m => m.Tag(WebhookMetrics.OutcomeTag))
            .Should().Equal(WebhookMetrics.OutcomeNetworkError, WebhookMetrics.OutcomeNetworkError, WebhookMetrics.OutcomeNetworkError);
    }

    [Fact(DisplayName = "5xx responses exhaust retries with a stable envelope and auto-disable at the failure limit")]
    public async Task ServerError_ExhaustsRetries_AndAutoDisables()
    {
        _receiver.Respond("/down", _ => new ResponsePlan(503, TimeSpan.Zero));
        Guid down = await AddSubscriptionAsync("/down", secret: Secret, maxConsecutiveFailures: 1);

        await StartAsync(retryDelay: TimeSpan.FromMilliseconds(50));
        _sut!.Enqueue("printer.offline", new { printerId = "p1" });

        await WaitUntilAsync(async () => !(await GetSubscriptionAsync(down)).IsActive);

        WebhookSubscription sub = await GetSubscriptionAsync(down);
        sub.ConsecutiveFailures.Should().Be(1);
        sub.LastSuccessAt.Should().BeNull();

        List<WebhookDeliveryLog> logs = await GetLogsAsync(down);
        logs.Select(l => l.Attempt).Should().Equal(1, 2, 3);
        logs.Should().OnlyContain(l => l.StatusCode == 503 && l.ErrorMessage == "HTTP 503" && !l.Success);

        List<ReceivedRequest> requests = _receiver.RequestsFor("/down");
        requests.Should().HaveCount(3);
        requests.Select(r => r.Body).Distinct().Should().ContainSingle("every attempt resends the same envelope, including its id");
        requests.Should().OnlyContain(r => r.Headers["X-Webhook-Signature"] == ExpectedSignature(r.Body));
        logs.Should().OnlyContain(l => l.Payload == requests[0].Body);
        Sum("webhook.deliveries_exhausted").Should().Be(1);
    }

    [Fact(DisplayName = "Success resets failures, signs the envelope, and emits bounded metrics")]
    public async Task Success_ResetsFailures_SignsPayload_AndEmitsBoundedMetrics()
    {
        _receiver.Respond("/ok", _ => new ResponsePlan(200, TimeSpan.Zero));
        Guid ok = await AddSubscriptionAsync("/ok", secret: Secret, consecutiveFailures: 4);

        await StartAsync();
        _sut!.Enqueue("job.completed", new { jobId = 7 });

        await WaitUntilAsync(async () => (await GetSubscriptionAsync(ok)).LastSuccessAt is not null);

        (await GetSubscriptionAsync(ok)).ConsecutiveFailures.Should().Be(0);
        WebhookDeliveryLog log = (await GetLogsAsync(ok)).Should().ContainSingle().Subject;
        log.Success.Should().BeTrue();
        log.StatusCode.Should().Be(200);

        ReceivedRequest request = _receiver.RequestsFor("/ok").Should().ContainSingle().Subject;
        request.Headers["X-Webhook-Event"].Should().Be("job.completed");
        request.Headers["X-Webhook-Signature"].Should().Be(ExpectedSignature(request.Body));
        request.Headers["Content-Type"].Should().StartWith("application/json");
        using (JsonDocument envelope = JsonDocument.Parse(request.Body))
        {
            envelope.RootElement.GetProperty("event").GetString().Should().Be("job.completed");
            envelope.RootElement.GetProperty("data").GetProperty("jobId").GetInt32().Should().Be(7);
            Guid.TryParse(envelope.RootElement.GetProperty("id").GetString(), out _).Should().BeTrue();
        }

        Measurements("webhook.queue_age_ms").Should().ContainSingle().Which.Value.Should().BeGreaterThanOrEqualTo(0);
        Sum("webhook.queue_depth").Should().Be(0);
        Measurements("webhook.attempt_duration_ms").Should().ContainSingle()
            .Which.Tag(WebhookMetrics.OutcomeTag).Should().Be(WebhookMetrics.OutcomeSuccess);

        RecordedMeasurement[] all = Snapshot();
        all.SelectMany(m => m.Tags.Select(t => t.Key)).Distinct()
            .Should().BeSubsetOf([WebhookMetrics.OutcomeTag, WebhookMetrics.ReasonTag]);
        all.SelectMany(m => m.Tags).Should().NotContain(t => (t.Value ?? string.Empty).Contains(PublicHost) || (t.Value ?? string.Empty).Contains(Secret));
    }

    [Fact(DisplayName = "A failed attempt followed by success resets the counter and keeps the same envelope")]
    public async Task RetryThenSuccess_ResetsCounter()
    {
        int calls = 0;
        _receiver.Respond("/flaky", _ => Interlocked.Increment(ref calls) == 1
            ? new ResponsePlan(500, TimeSpan.Zero)
            : new ResponsePlan(204, TimeSpan.Zero));
        Guid flaky = await AddSubscriptionAsync("/flaky", consecutiveFailures: 2);

        await StartAsync(retryDelay: TimeSpan.FromMilliseconds(50));
        _sut!.Enqueue("job.started", new { jobId = 3 });

        await WaitUntilAsync(async () => (await GetSubscriptionAsync(flaky)).LastSuccessAt is not null);

        (await GetSubscriptionAsync(flaky)).ConsecutiveFailures.Should().Be(0);
        List<WebhookDeliveryLog> logs = await GetLogsAsync(flaky);
        logs.Select(l => (l.Attempt, l.Success, l.StatusCode)).Should().Equal((1, false, 500), (2, true, 204));
        _receiver.RequestsFor("/flaky").Select(r => r.Body).Distinct().Should().ContainSingle();
        Sum("webhook.deliveries_exhausted").Should().Be(0);
    }

    [Fact(DisplayName = "Retry backoff does not block later events or other subscribers")]
    public async Task RetryBackoff_DoesNotBlockDrainLoop()
    {
        _receiver.Respond("/down", _ => new ResponsePlan(503, TimeSpan.Zero));
        _receiver.Respond("/up", _ => new ResponsePlan(200, TimeSpan.Zero));
        Guid down = await AddSubscriptionAsync("/down");
        await AddSubscriptionAsync("/up");

        // Long backoff: the pre-#3344 inline Task.Delay would hold /up's first delivery for 30 s+.
        await StartAsync(retryDelay: TimeSpan.FromSeconds(30));
        _sut!.Enqueue("job.started", new { jobId = 1 });
        _sut.Enqueue("job.completed", new { jobId = 1 });

        await WaitUntilAsync(() => Task.FromResult(_receiver.RequestsFor("/up").Count == 2), TimeSpan.FromSeconds(5));

        _receiver.RequestsFor("/up").Select(r => r.Headers["X-Webhook-Event"]).Should().Equal("job.started", "job.completed");
        await WaitUntilAsync(() => Task.FromResult(Sum("webhook.pending_retries") == 2), TimeSpan.FromSeconds(5));
        _receiver.RequestsFor("/down").Select(r => r.Headers["X-Webhook-Event"]).Should().Equal("job.started", "job.completed");
        (await GetSubscriptionAsync(down)).ConsecutiveFailures.Should().Be(0, "retries are pending, not exhausted");
    }

    [Fact(DisplayName = "Intentional ordering change: a later event can reach a subscriber before an earlier event's retry")]
    public async Task Retry_IsDeliveredAfterLaterEvent()
    {
        int calls = 0;
        _receiver.Respond("/order", _ => Interlocked.Increment(ref calls) == 1
            ? new ResponsePlan(503, TimeSpan.Zero)
            : new ResponsePlan(200, TimeSpan.Zero));
        Guid sub = await AddSubscriptionAsync("/order");

        // Both events are queued before the loop starts and the retry window is wide, so the
        // second event is always dequeued before the first event's retry becomes due.
        _sut = CreateSut(retryDelay: TimeSpan.FromSeconds(3));
        _sut.Enqueue("job.started", new { seq = 1 });
        _sut.Enqueue("job.completed", new { seq = 2 });
        await _sut.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => Task.FromResult(_receiver.RequestsFor("/order").Count == 3));

        _receiver.RequestsFor("/order").Select(r => r.Headers["X-Webhook-Event"])
            .Should().Equal("job.started", "job.completed", "job.started");
        await WaitUntilAsync(async () => (await GetLogsAsync(sub)).Count == 3);
        (await GetSubscriptionAsync(sub)).ConsecutiveFailures.Should().Be(0);
    }

    [Theory(DisplayName = "A pending retry is abandoned without counting a failure when the subscription changes")]
    [InlineData("disable", WebhookMetrics.ReasonSubscriptionInactive)]
    [InlineData("delete", WebhookMetrics.ReasonSubscriptionRemoved)]
    [InlineData("unsubscribe", WebhookMetrics.ReasonEventTypeUnsubscribed)]
    public async Task PendingRetry_IsAbandoned_WhenSubscriptionChanges(string change, string expectedReason)
    {
        _receiver.Respond("/down", _ => new ResponsePlan(503, TimeSpan.Zero));
        Guid down = await AddSubscriptionAsync("/down", eventTypes: "job.failed,job.completed");

        // Wide retry window so the subscription change always lands before the retry is due.
        await StartAsync(retryDelay: TimeSpan.FromSeconds(3));
        _sut!.Enqueue("job.failed", new { jobId = 9 });

        await WaitUntilAsync(async () => (await GetLogsAsync(down)).Count == 1);
        await using (var db = new AppDbContext(_dbOptions))
        {
            WebhookSubscription entity = await db.WebhookSubscriptions.SingleAsync(w => w.Id == down);
            switch (change)
            {
                case "disable":
                    entity.IsActive = false;
                    break;
                case "delete":
                    db.WebhookSubscriptions.Remove(entity);
                    break;
                default:
                    entity.EventTypes = "job.completed";
                    break;
            }

            await db.SaveChangesAsync();
        }

        Sum("webhook.pending_retries").Should().Be(1, "the change must be persisted before the retry runs");
        await WaitUntilAsync(() => Task.FromResult(Measurements("webhook.deliveries_dropped").Count == 1));

        Measurements("webhook.deliveries_dropped").Single().Tag(WebhookMetrics.ReasonTag).Should().Be(expectedReason);
        _receiver.RequestsFor("/down").Should().ContainSingle();
        Sum("webhook.pending_retries").Should().Be(0);
        Sum("webhook.deliveries_exhausted").Should().Be(0);
        await using (var db = new AppDbContext(_dbOptions))
        {
            WebhookSubscription? after = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(w => w.Id == down);
            if (change == "delete")
            {
                after.Should().BeNull();
            }
            else
            {
                after!.ConsecutiveFailures.Should().Be(0);
                (await GetLogsAsync(down)).Should().ContainSingle();
            }
        }
    }

    [Fact(DisplayName = "When the retry buffer is full the delivery is dropped and counted as a failed delivery")]
    public async Task RetryBufferFull_DropsAndCountsFailure()
    {
        _receiver.Respond("/down", _ => new ResponsePlan(503, TimeSpan.Zero));
        Guid down = await AddSubscriptionAsync("/down");

        await StartAsync(retryDelay: TimeSpan.FromSeconds(30), maxPendingRetries: 1);
        _sut!.Enqueue("job.started", new { jobId = 1 });
        _sut.Enqueue("job.completed", new { jobId = 1 });

        await WaitUntilAsync(async () => (await GetSubscriptionAsync(down)).ConsecutiveFailures == 1);

        Measurements("webhook.deliveries_dropped").Should().ContainSingle()
            .Which.Tag(WebhookMetrics.ReasonTag).Should().Be(WebhookMetrics.ReasonRetryQueueFull);
        Sum("webhook.pending_retries").Should().Be(1);
        _receiver.RequestsFor("/down").Should().HaveCount(2);
    }

    [Fact(DisplayName = "A full event queue drops new events and reports queue_full")]
    public void QueueFull_DropsEvent_AndRecordsMetric()
    {
        _sut = CreateSut(maxQueueSize: 2);

        _sut.Enqueue("job.started", new { n = 1 });
        _sut.Enqueue("job.started", new { n = 2 });
        _sut.Enqueue("job.started", new { n = 3 });

        Sum("webhook.queue_depth").Should().Be(2);
        Measurements("webhook.events_dropped").Should().ContainSingle()
            .Which.Tag(WebhookMetrics.ReasonTag).Should().Be(WebhookMetrics.ReasonQueueFull);
    }

    [Fact(DisplayName = "Private destinations are still blocked before any request is sent, without retries")]
    public async Task PrivateDestination_IsBlocked_WithoutRetry()
    {
        Guid blocked = await AddSubscriptionAsync("/hook", host: "127.0.0.1");

        await StartAsync(retryDelay: TimeSpan.FromMilliseconds(50));
        _sut!.Enqueue("job.completed", new { jobId = 4 });

        await WaitUntilAsync(async () => (await GetSubscriptionAsync(blocked)).ConsecutiveFailures == 1);
        await Task.Delay(300);

        WebhookDeliveryLog log = (await GetLogsAsync(blocked)).Should().ContainSingle().Subject;
        log.ErrorMessage.Should().Be("URL resolves to a private or reserved IP address");
        _receiver.AllRequests.Should().BeEmpty();
        Measurements("webhook.attempt_duration_ms").Should().ContainSingle()
            .Which.Tag(WebhookMetrics.OutcomeTag).Should().Be(WebhookMetrics.OutcomeBlockedDestination);
    }

    [Fact(DisplayName = "An unexpected non-HTTP failure is isolated to its delivery: later subscribers and events still deliver")]
    public async Task UnexpectedPersistenceFailure_IsIsolatedToOneDelivery()
    {
        _receiver.Respond("/poisoned", _ => new ResponsePlan(200, TimeSpan.Zero));
        _receiver.Respond("/healthy", _ => new ResponsePlan(200, TimeSpan.Zero));

        // Inserted first so the failing delivery is attempted before the healthy one.
        Guid poisoned = await AddSubscriptionAsync("/poisoned");
        Guid healthy = await AddSubscriptionAsync("/healthy");
        _poison.PoisonedSubscriptionId = poisoned;

        await StartAsync(retryDelay: TimeSpan.FromMilliseconds(50));
        _sut!.Enqueue("job.started", new { jobId = 7 });
        _sut.Enqueue("job.completed", new { jobId = 7 });

        await WaitUntilAsync(async () => (await GetLogsAsync(healthy)).Count == 2);

        // The throw happens while saving the delivery log, after the HTTP exchange and outside
        // its handler, so only the per-delivery isolation boundary can contain it.
        _receiver.RequestsFor("/poisoned").Select(r => r.Headers["X-Webhook-Event"]).Should().Equal("job.started", "job.completed");
        _receiver.RequestsFor("/healthy").Select(r => r.Headers["X-Webhook-Event"]).Should().Equal("job.started", "job.completed");
        await WaitUntilAsync(() => Task.FromResult(Sum("webhook.isolated_delivery_failures") == 2));
        (await GetLogsAsync(poisoned)).Should().BeEmpty();
        (await GetSubscriptionAsync(healthy)).ConsecutiveFailures.Should().Be(0);
        _sut.ExecuteTask!.IsCompleted.Should().BeFalse("the drain loop must survive an isolated failure");
    }

    [Fact(DisplayName = "Shutdown cancels an in-flight delivery promptly and does not record it as a failure")]
    public async Task Shutdown_CancelsInFlightDelivery_WithoutCountingFailure()
    {
        _receiver.Respond("/hang", _ => new ResponsePlan(200, TimeSpan.FromSeconds(30)));
        Guid hang = await AddSubscriptionAsync("/hang");

        await StartAsync(httpTimeout: TimeSpan.FromSeconds(60));
        _sut!.Enqueue("job.completed", new { jobId = 5 });
        await WaitUntilAsync(() => Task.FromResult(_receiver.RequestsFor("/hang").Count == 1));

        // Queued behind the in-flight delivery; discarded at shutdown.
        _sut.Enqueue("job.failed", new { jobId = 6 });
        Sum("webhook.queue_depth").Should().Be(1);

        var stopwatch = Stopwatch.StartNew();
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await _sut.StopAsync(stopCts.Token);
        }

        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        _sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        (await GetLogsAsync(hang)).Should().BeEmpty();
        WebhookSubscription sub = await GetSubscriptionAsync(hang);
        sub.ConsecutiveFailures.Should().Be(0);
        sub.LastDeliveryAt.Should().BeNull();
        Measurements("webhook.attempt_duration_ms").Should().BeEmpty();
        Measurements("webhook.isolated_delivery_failures").Should().BeEmpty();
        Sum("webhook.queue_depth").Should().Be(0, "discarded queued events are removed from the gauge");
        Sum("webhook.pending_retries").Should().Be(0);
        _receiver.RequestsFor("/hang").Should().ContainSingle();
    }

    private WebhookService CreateSut(
        TimeSpan? httpTimeout = null,
        TimeSpan? retryDelay = null,
        int maxPendingRetries = 100,
        int maxQueueSize = 100)
    {
        _httpClientFactory = new RedirectingHttpClientFactory(
            _receiver.Port,
            LoopbackReceiver.GetClosedPort(),
            httpTimeout ?? TimeSpan.FromSeconds(10));

        TimeSpan delay = retryDelay ?? TimeSpan.FromMilliseconds(50);
        var tuning = new WebhookDeliveryTuning(
            [delay, delay],
            maxPendingRetries,
            maxQueueSize,
            TimeSpan.FromMilliseconds(20));

        return new WebhookService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _httpClientFactory,
            Mock.Of<ISensitiveDataProtector>(MockBehavior.Strict),
            NullLogger<WebhookService>.Instance,
            _metrics,
            tuning);
    }

    private async Task StartAsync(
        TimeSpan? httpTimeout = null,
        TimeSpan? retryDelay = null,
        int maxPendingRetries = 100)
    {
        _sut = CreateSut(httpTimeout, retryDelay, maxPendingRetries);
        await _sut.StartAsync(CancellationToken.None);
    }

    private async Task<Guid> AddSubscriptionAsync(
        string path,
        int port = 80,
        string host = PublicHost,
        string? secret = null,
        string eventTypes = "*",
        int consecutiveFailures = 0,
        int maxConsecutiveFailures = 10)
    {
        var sub = new WebhookSubscription
        {
            Name = path,
            Url = port == 80 ? $"http://{host}{path}" : $"http://{host}:{port}{path}",
            Secret = secret,
            EventTypes = eventTypes,
            ConsecutiveFailures = consecutiveFailures,
            MaxConsecutiveFailures = maxConsecutiveFailures,
        };

        await using var db = new AppDbContext(_dbOptions);
        db.WebhookSubscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private async Task<WebhookSubscription> GetSubscriptionAsync(Guid id)
    {
        await using var db = new AppDbContext(_dbOptions);
        return await db.WebhookSubscriptions.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private async Task<List<WebhookDeliveryLog>> GetLogsAsync(Guid subscriptionId)
    {
        await using var db = new AppDbContext(_dbOptions);
        List<WebhookDeliveryLog> logs = await db.WebhookDeliveryLogs.AsNoTracking()
            .Where(l => l.WebhookSubscriptionId == subscriptionId)
            .ToListAsync();
        return logs.OrderBy(l => l.CreatedAt).ThenBy(l => l.Attempt).ToList();
    }

    private static string ExpectedSignature(string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? WaitTimeout;
        while (!await condition())
        {
            if (deadline.Elapsed > limit)
            {
                throw new TimeoutException($"Condition not met within {limit}.");
            }

            await Task.Delay(25);
        }
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new KeyValuePair<string, string?>[tags.Length];
        for (int i = 0; i < tags.Length; i++)
        {
            copy[i] = new KeyValuePair<string, string?>(tags[i].Key, tags[i].Value?.ToString());
        }

        lock (_measurementsLock)
        {
            _measurements.Add(new RecordedMeasurement(instrument.Name, value, copy));
        }
    }

    private RecordedMeasurement[] Snapshot()
    {
        lock (_measurementsLock)
        {
            return _measurements.ToArray();
        }
    }

    private List<RecordedMeasurement> Measurements(string name) =>
        Snapshot().Where(m => m.Name == name).ToList();

    private double Sum(string name) => Measurements(name).Sum(m => m.Value);

    private sealed record RecordedMeasurement(string Name, double Value, KeyValuePair<string, string?>[] Tags)
    {
        public string? Tag(string key) => Tags.FirstOrDefault(t => t.Key == key).Value;
    }

    private sealed record ResponsePlan(int Status, TimeSpan Delay);

    private sealed record ReceivedRequest(string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    /// <summary>
    /// Minimal HTTP/1.1 server on loopback that records each request and replies after a
    /// configurable delay with a configurable status.
    /// </summary>
    private sealed class LoopbackReceiver : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, Func<ReceivedRequest, ResponsePlan>> _routes = new();
        private readonly ConcurrentQueue<ReceivedRequest> _requests = new();

        public LoopbackReceiver()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public ReceivedRequest[] AllRequests => _requests.ToArray();

        public static int GetClosedPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Respond(string path, Func<ReceivedRequest, ResponsePlan> plan) => _routes[path] = plan;

        public List<ReceivedRequest> RequestsFor(string path) =>
            _requests.Where(r => r.Path == path).ToList();

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }

                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    ReceivedRequest request = await ReadRequestAsync(stream, _cts.Token);
                    _requests.Enqueue(request);

                    ResponsePlan plan = _routes.TryGetValue(request.Path, out var route)
                        ? route(request)
                        : new ResponsePlan(404, TimeSpan.Zero);
                    if (plan.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(plan.Delay, _cts.Token);
                    }

                    byte[] response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {plan.Status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, _cts.Token);
                }
                catch (Exception)
                {
                    // Client timed out/aborted or the receiver is shutting down.
                }
            }
        }

        private static async Task<ReceivedRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    throw new IOException("Connection closed before headers completed.");
                }

                buffer.Write(chunk, 0, read);
                headerEnd = IndexOfHeaderTerminator(buffer.GetBuffer(), (int)buffer.Length);
            }

            byte[] raw = buffer.ToArray();
            string head = Encoding.ASCII.GetString(raw, 0, headerEnd);
            string[] lines = head.Split("\r\n");
            string path = lines[0].Split(' ')[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            int contentLength = headers.TryGetValue("Content-Length", out string? len) ? int.Parse(len) : 0;
            var body = new MemoryStream();
            body.Write(raw, headerEnd + 4, raw.Length - headerEnd - 4);
            while (body.Length < contentLength)
            {
                int read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }

                body.Write(chunk, 0, read);
            }

            return new ReceivedRequest(path, headers, Encoding.UTF8.GetString(body.ToArray()));
        }

        private static int IndexOfHeaderTerminator(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Returns real <see cref="HttpClient"/> instances whose TCP connect is redirected to
    /// loopback: URL port 80 reaches the receiver, port 81 reaches a closed port (refused).
    /// </summary>
    /// <summary>Throws while saving a delivery log for one chosen subscription.</summary>
    private sealed class PoisonDeliveryLogInterceptor : SaveChangesInterceptor
    {
        public Guid? PoisonedSubscriptionId { get; set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfPoisoned(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfPoisoned(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void ThrowIfPoisoned(DbContext? context)
        {
            if (PoisonedSubscriptionId is { } id && context is not null &&
                context.ChangeTracker.Entries<WebhookDeliveryLog>().Any(e => e.Entity.WebhookSubscriptionId == id))
            {
                throw new InvalidOperationException("Simulated persistence failure");
            }
        }
    }

    private sealed class RedirectingHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly SocketsHttpHandler _handler;
        private readonly TimeSpan _timeout;

        public RedirectingHttpClientFactory(int receiverPort, int closedPort, TimeSpan timeout)
        {
            _timeout = timeout;
            _handler = new SocketsHttpHandler
            {
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = async (context, ct) =>
                {
                    int target = context.DnsEndPoint.Port == RefusedUrlPort ? closedPort : receiverPort;
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, target), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
        }

        public HttpClient CreateClient(string name)
        {
            name.Should().Be("WebhookDelivery");
            return new HttpClient(_handler, disposeHandler: false) { Timeout = _timeout };
        }

        public void Dispose() => _handler.Dispose();
    }
}
