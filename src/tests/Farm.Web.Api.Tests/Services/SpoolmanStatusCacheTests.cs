using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests.TestHelpers;
using Microsoft.Extensions.Caching.Memory;
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
        var cache = new SpoolmanStatusCache(memoryCache, timeProvider);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource<SpoolmanSpoolDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCount = 0;

        Task<SpoolmanSpoolDto?> FetchAsync(int spoolId, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref requestCount);
            requestStarted.SetResult();
            return releaseRequest.Task;
        }

        Task<SpoolmanSpoolDto?> first = cache.GetSpoolAsync(42, FetchAsync, CancellationToken.None);
        await requestStarted.Task;
        Task<SpoolmanSpoolDto?> second = cache.GetSpoolAsync(42, FetchAsync, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref requestCount));

        var expected = new SpoolmanSpoolDto(42, "PLA", "PLA", 900, null, false);
        releaseRequest.SetResult(expected);

        SpoolmanSpoolDto?[] results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Same(expected, result));
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GetSpoolAsync_EntryExpires_RefreshesUpstreamValue()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var timeProvider = new MutableTimeProvider(Now);
        var cache = new SpoolmanStatusCache(memoryCache, timeProvider);
        var first = new SpoolmanSpoolDto(7, "First", "PLA", 900, null, false);
        var refreshed = new SpoolmanSpoolDto(7, "Refreshed", "PLA", 850, null, false);
        int requestCount = 0;

        Task<SpoolmanSpoolDto?> FetchAsync(int spoolId, CancellationToken ct)
        {
            int currentRequest = Interlocked.Increment(ref requestCount);
            return Task.FromResult<SpoolmanSpoolDto?>(currentRequest == 1 ? first : refreshed);
        }

        SpoolmanSpoolDto? cached = await cache.GetSpoolAsync(7, FetchAsync, CancellationToken.None);
        timeProvider.Advance(SpoolmanStatusCache.CacheTtl.Add(TimeSpan.FromMilliseconds(1)));
        SpoolmanSpoolDto? afterExpiry = await cache.GetSpoolAsync(7, FetchAsync, CancellationToken.None);

        Assert.Same(first, cached);
        Assert.Same(refreshed, afterExpiry);
        Assert.Equal(2, requestCount);
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
        var service = new SpoolmanService(http, settings.Object, NullLogger<SpoolmanService>.Instance);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var statusCache = new SpoolmanStatusCache(memoryCache, new MutableTimeProvider(Now));

        SpoolmanSpoolDto? cachedStatus = await statusCache.GetSpoolAsync(
            SpoolId,
            service.GetSpoolByIdAsync,
            CancellationToken.None);
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
