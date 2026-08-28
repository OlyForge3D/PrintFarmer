using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public sealed class SpoolmanStatusCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSpoolAsync_ConcurrentMisses_CoalescesUpstreamRequest()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var timeProvider = new MutableTimeProvider(Now);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<SpoolmanSpoolDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<ISpoolmanService> spoolman = new();
        // IDISP013 false positive: this callback is only invoked later when the mocked
        // method is called during the test, not at setup time.
#pragma warning disable IDISP013 // Await in using
        _ = spoolman
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                requestStarted.SetResult();
#pragma warning disable VSTHRD003 // releaseRequest is a TaskCompletionSource this test controls to hold the mocked spool lookup open; not a foreign/UI-thread task.
                return releaseRequest.Task;
#pragma warning restore VSTHRD003
            });
#pragma warning restore IDISP013
        using ServiceProvider services = BuildServices(spoolman.Object);
        var cache = new SpoolmanStatusCache(
            memoryCache,
            timeProvider,
            services.GetRequiredService<IServiceScopeFactory>());

        Task<SpoolmanSpoolDto?> first = cache.GetSpoolAsync(42, CancellationToken.None);
        await requestStarted.Task;
        Task<SpoolmanSpoolDto?> second = cache.GetSpoolAsync(42, CancellationToken.None);

        spoolman.Verify(
            service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()),
            Times.Once);

        var expected = new SpoolmanSpoolDto(42, "PLA", "PLA", 900, null, false);
        releaseRequest.SetResult(expected);

        SpoolmanSpoolDto?[] results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Same(expected, result));
        spoolman.Verify(
            service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSpoolAsync_EntryExpires_RefreshesUpstreamValue()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var timeProvider = new MutableTimeProvider(Now);
        var first = new SpoolmanSpoolDto(7, "First", "PLA", 900, null, false);
        var refreshed = new SpoolmanSpoolDto(7, "Refreshed", "PLA", 850, null, false);
        Mock<ISpoolmanService> spoolman = new();
        // IDISP013 false positive: the setup lambda is an expression tree describing which
        // member to intercept; it is never invoked at this point.
#pragma warning disable IDISP013 // Await in using
        _ = spoolman
            .SetupSequence(service => service.GetSpoolByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(first)
            .ReturnsAsync(refreshed);
#pragma warning restore IDISP013
        using ServiceProvider services = BuildServices(spoolman.Object);
        var cache = new SpoolmanStatusCache(
            memoryCache,
            timeProvider,
            services.GetRequiredService<IServiceScopeFactory>());

        SpoolmanSpoolDto? cached = await cache.GetSpoolAsync(7, CancellationToken.None);
        timeProvider.Advance(SpoolmanStatusCache.CacheTtl.Add(TimeSpan.FromMilliseconds(1)));
        SpoolmanSpoolDto? afterExpiry = await cache.GetSpoolAsync(7, CancellationToken.None);

        Assert.Same(first, cached);
        Assert.Same(refreshed, afterExpiry);
        spoolman.Verify(
            service => service.GetSpoolByIdAsync(7, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetSpoolAsync_LeaderCancels_FollowerReceivesSharedResult()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<SpoolmanSpoolDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<ISpoolmanService> spoolman = new();
        // IDISP013 false positive: this callback is only invoked later when the mocked
        // method is called during the test, not at setup time.
#pragma warning disable IDISP013 // Await in using
        _ = spoolman
            .Setup(service => service.GetSpoolByIdAsync(21, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                requestStarted.SetResult();
#pragma warning disable VSTHRD003 // releaseRequest is a TaskCompletionSource this test controls to hold the mocked spool lookup open; not a foreign/UI-thread task.
                return releaseRequest.Task;
#pragma warning restore VSTHRD003
            });
#pragma warning restore IDISP013
        using ServiceProvider services = BuildServices(spoolman.Object);
        var cache = new SpoolmanStatusCache(
            memoryCache,
            new MutableTimeProvider(Now),
            services.GetRequiredService<IServiceScopeFactory>());
        using var leaderCancellation = new CancellationTokenSource();

        Task<SpoolmanSpoolDto?> leader = cache.GetSpoolAsync(21, leaderCancellation.Token);
        await requestStarted.Task;
        Task<SpoolmanSpoolDto?> follower = cache.GetSpoolAsync(21, CancellationToken.None);
        leaderCancellation.Cancel();

#pragma warning disable VSTHRD003 // leader was started earlier in this method; awaiting it here asserts the pending call observes cancellation, not a foreign/UI-thread task.
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
#pragma warning restore VSTHRD003

        var expected = new SpoolmanSpoolDto(21, "PETG", "PETG", 600, null, false);
        releaseRequest.SetResult(expected);

        Assert.Same(expected, await follower);
        spoolman.Verify(
            service => service.GetSpoolByIdAsync(21, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetSpoolAsync_UpstreamReturnsNull_RetriesNextRequest()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var expected = new SpoolmanSpoolDto(31, "ABS", "ABS", 700, null, false);
        Mock<ISpoolmanService> spoolman = new();
        // IDISP013 false positive: the setup lambda is an expression tree describing which
        // member to intercept; it is never invoked at this point.
#pragma warning disable IDISP013 // Await in using
        _ = spoolman
            .SetupSequence(service => service.GetSpoolByIdAsync(31, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanSpoolDto?)null)
            .ReturnsAsync(expected);
#pragma warning restore IDISP013
        using ServiceProvider services = BuildServices(spoolman.Object);
        var cache = new SpoolmanStatusCache(
            memoryCache,
            new MutableTimeProvider(Now),
            services.GetRequiredService<IServiceScopeFactory>());

        SpoolmanSpoolDto? missing = await cache.GetSpoolAsync(31, CancellationToken.None);
        SpoolmanSpoolDto? retried = await cache.GetSpoolAsync(31, CancellationToken.None);

        Assert.Null(missing);
        Assert.Same(expected, retried);
        spoolman.Verify(
            service => service.GetSpoolByIdAsync(31, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ConsumeFilamentAsync_AfterCachedStatusRead_UsesFreshSpoolWeight()
    {
        const int SpoolId = 12;
        double upstreamUsedWeight = 10;
        int getRequestCount = 0;
        string? patchBody = null;

        using var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                _ = Interlocked.Increment(ref getRequestCount);
                string spoolJson = JsonSerializer.Serialize(new
                {
                    id = SpoolId,
                    name = "Shared PLA",
                    material = "PLA",
                    used_weight = upstreamUsedWeight,
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(spoolJson, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Patch)
            {
                patchBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        Mock<ISettingsService> settings = new();
        _ = settings
            .Setup(service => service.Get<SpoolmanSettings>())
            .Returns(new SpoolmanSettings { BaseUrl = "http://spoolman.local" });
        var service = new SpoolmanService(http, settings.Object, NullLogger<SpoolmanService>.Instance, Farm.Testing.Shared.AppDbTestHelpers.PermissiveEgressGuard());
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        using ServiceProvider services = BuildServices(service);
        var statusCache = new SpoolmanStatusCache(
            memoryCache,
            new MutableTimeProvider(Now),
            services.GetRequiredService<IServiceScopeFactory>());

        SpoolmanSpoolDto? cachedStatus = await statusCache.GetSpoolAsync(SpoolId, CancellationToken.None);
        upstreamUsedWeight = 25;
        bool consumed = await service.ConsumeFilamentAsync(SpoolId, 5, CancellationToken.None);

        Assert.NotNull(cachedStatus);
        Assert.Equal(10, cachedStatus.UsedWeightG);
        Assert.True(consumed);
        Assert.Equal(2, getRequestCount);
        Assert.NotNull(patchBody);

        using JsonDocument patch = JsonDocument.Parse(patchBody);
        Assert.Equal(30, patch.RootElement.GetProperty("used_weight").GetDouble());
    }

    private static ServiceProvider BuildServices(ISpoolmanService spoolmanService)
    {
        var services = new ServiceCollection();
        _ = services.AddScoped(_ => spoolmanService);
        return services.BuildServiceProvider();
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow = _utcNow.Add(elapsed);
        }
    }
}
