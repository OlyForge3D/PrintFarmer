using System.Diagnostics;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Health;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Health;

public sealed class ObicoHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Disabled_DoesNotResolveDatabaseOrProbe()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(service => service.Get<ObicoSettings>()).Returns(new ObicoSettings());
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton(settings.Object).BuildServiceProvider();
        var check = new ObicoHealthCheck(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ObicoHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("Obico failure detection disabled");
        result.Data.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object>("enabled", false));
    }

    [Theory]
    [InlineData(true, true, HealthStatus.Healthy, 1)]
    [InlineData(true, false, HealthStatus.Degraded, 0)]
    [InlineData(false, false, HealthStatus.Degraded, 0)]
    public async Task CheckHealthAsync_RegisteredPool_ReportsOnlyEligiblePoolAvailability(bool globalHealthy, bool poolHealthy, HealthStatus status, int reachable)
    {
        using var fixture = new Fixture();
        await fixture.AddServerAsync("http://pool.invalid", "pool-key");
        fixture.Probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, string? _, CancellationToken _) =>
                new ObicoConnectivityResult(url.Contains("global", StringComparison.Ordinal) ? globalHealthy : poolHealthy, 1, "private diagnostics"));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(status);
        result.Data["enabled"].Should().Be(true);
        result.Data["targetCount"].Should().Be(1);
        result.Data["reachableCount"].Should().Be(reachable);
        if (reachable == 0)
        {
            result.Description.Should().Contain("unavailable");
        }

        fixture.Probe.Verify(probe => probe.ProbeAsync("http://global.invalid", null, It.IsAny<CancellationToken>()), Times.Never);
        fixture.Probe.Verify(probe => probe.ProbeAsync("http://pool.invalid", "pool-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckHealthAsync_DuplicateAndDisabledServers_ProbesOnlyDistinctEnabledRoutes()
    {
        using var fixture = new Fixture();
        await fixture.AddServerAsync("http://GLOBAL.invalid/", null);
        await fixture.AddServerAsync("http://global.invalid", null);
        await fixture.AddServerAsync("http://global.invalid", "separate-credentials");
        await fixture.AddServerAsync("http://disabled.invalid", null, enabled: false);
        fixture.Probe.Setup(probe => probe.ProbeAsync("http://global.invalid", "separate-credentials", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ObicoConnectivityResult(false, 0, "Invalid credentials"));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["targetCount"].Should().Be(2);
        result.Data["reachableCount"].Should().Be(1);
        fixture.Probe.Verify(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Probe.Verify(probe => probe.ProbeAsync("http://disabled.invalid", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_PoolOnlyOrNoTargets_DoesNotInventGlobalFallback()
    {
        using var fixture = new Fixture();
        fixture.Settings.ObicoApiUrl = "";
        HealthCheckResult empty = await fixture.Check.CheckHealthAsync(new());
        empty.Status.Should().Be(HealthStatus.Degraded);
        empty.Description.Should().Contain("unavailable");
        empty.Data["targetCount"].Should().Be(0);

        await fixture.AddServerAsync("http://pool.invalid", null);
        fixture.Clock.Now += ObicoHealthCheck.CacheDuration;
        HealthCheckResult pooled = await fixture.Check.CheckHealthAsync(new());
        pooled.Status.Should().Be(HealthStatus.Healthy);
        pooled.Data["targetCount"].Should().Be(1);
    }

#pragma warning disable IDISP013, VSTHRD003 // Deliberate concurrent callers; every task completes before fixture disposal.
    [Fact]
    public async Task CheckHealthAsync_ConcurrentRequestsAndCancelledCaller_SharesAndCachesProbe()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ObicoConnectivityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string? _, CancellationToken _) =>
            {
                started.TrySetResult();
                return finish.Task;
            });
        using var caller = new CancellationTokenSource();
        Task<HealthCheckResult> cancelled = fixture.Check.CheckHealthAsync(new(), caller.Token);
        await started.Task;
        Task<HealthCheckResult>[] concurrent = Enumerable.Range(0, 20)
            .Select(_ => fixture.Check.CheckHealthAsync(new())).ToArray();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        finish.SetResult(new(false, 0, "private"));
        HealthCheckResult[] results = await Task.WhenAll(concurrent);
        results.Should().OnlyContain(result => result.Status == HealthStatus.Degraded);
        await fixture.Check.CheckHealthAsync(new());
        fixture.Probe.Verify(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        fixture.Clock.Now += ObicoHealthCheck.CacheDuration;
        await fixture.Check.CheckHealthAsync(new());
        fixture.Probe.Verify(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Settings.Enabled = false;
        (await fixture.Check.CheckHealthAsync(new())).Data["enabled"].Should().Be(false);
    }
#pragma warning restore IDISP013, VSTHRD003

    [Fact]
    public async Task CheckHealthAsync_ManyStalledTargets_HasBoundedAggregateDurationAndConcurrency()
    {
        using var fixture = new Fixture();
        for (int i = 0; i < 10; i++)
        {
            await fixture.AddServerAsync($"http://pool-{i}.invalid", null);
        }

        int started = 0;
        fixture.Probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string? _, CancellationToken ct) =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ObicoConnectivityResult(true, 0, null);
            });
        var elapsed = Stopwatch.StartNew();

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new()).WaitAsync(TimeSpan.FromSeconds(9));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["targetCount"].Should().Be(10);
        result.Data["reachableCount"].Should().Be(0);
        started.Should().Be(4);
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task CheckHealthAsync_DisabledPool_DoesNotAdvertiseGlobalOverflow()
    {
        using var fixture = new Fixture();
        await fixture.AddServerAsync("http://pool.invalid", null, enabled: false);

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("unavailable");
        result.Data["targetCount"].Should().Be(0);
        fixture.Probe.Verify(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_PreExistingGlobalPrintAfterPoolAdded_IncludesItsDestination()
    {
        using var fixture = new Fixture();
        await fixture.AddServerAsync("http://pool.invalid", null);
        using (IServiceScope scope = fixture.Provider.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PrinterServiceStates.Add(new PrinterServiceState
            {
                PrinterId = Guid.NewGuid(),
                ObicoPrintKey = "external:print",
                ObicoFallbackUrl = fixture.Settings.ObicoApiUrl
            });
            await db.SaveChangesAsync();
        }

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Data["targetCount"].Should().Be(2);
        fixture.Probe.Verify(probe => probe.ProbeAsync(fixture.Settings.ObicoApiUrl, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckHealthAsync_EgressDenied_AnonymousResponseContainsOnlyGenericData()
    {
        using var fixture = new Fixture();
        var guard = new Mock<IEgressGuard>();
        guard.Setup(value => value.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EgressCheckResult.Deny("secret endpoint and credential diagnostics"));
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var actualProbe = new ObicoConnectivityProbe(factory.Object, guard.Object, NullLogger<ObicoConnectivityProbe>.Instance);
        fixture.Probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string url, string? key, CancellationToken ct) => await actualProbe.ProbeAsync(url, key, ct));
        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["obico"] = new(result.Status, result.Description, TimeSpan.Zero, result.Exception, result.Data)
        }, TimeSpan.Zero);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await ProgramHelpers.WriteHealthResponseAsync(context, report, null, Mock.Of<IHostEnvironment>());
        body.Position = 0;
        string json = await new StreamReader(body).ReadToEndAsync();

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().BeNull();
        using JsonDocument document = JsonDocument.Parse(json);
        json.Should().Contain("unavailable").And.Contain("targetCount").And.Contain("reachableCount");
        json.Should().NotContain("global.invalid").And.NotContain("secret").And.NotContain("credential");
        result.Data.Keys.Should().BeEquivalentTo("enabled", "targetCount", "reachableCount");
        factory.Verify(value => value.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeThrows_DoesNotExposeExceptionOrPoisonCache()
    {
        using var fixture = new Fixture();
        fixture.Probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("private URL / credential details"));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().BeNull();
        result.Description.Should().Be("Obico failure detection availability could not be determined");
        JsonSerializer.Serialize(result).Should().NotContain("private").And.NotContain("credential");
        await fixture.Check.CheckHealthAsync(new());
        fixture.Probe.Verify(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Registration_HealthServiceResolutions_UseOneSingleton()
    {
        using ServiceProvider provider = new ServiceCollection().AddLogging().AddPrintFarmerHealthChecks().BuildServiceProvider();
        HealthCheckRegistration registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(value => value.Name == "obico");
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();
        registration.Factory(first.ServiceProvider).Should().BeSameAs(registration.Factory(second.ServiceProvider));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public ObicoSettings Settings { get; } = new() { Enabled = true, ObicoApiUrl = "http://global.invalid" };
        public Mock<IObicoConnectivityProbe> Probe { get; } = new();
        public Clock Clock { get; } = new();
        public ServiceProvider Provider { get; }
        public ObicoHealthCheck Check { get; }

        public Fixture()
        {
            var settings = new Mock<ISettingsService>();
            settings.Setup(service => service.Get<ObicoSettings>()).Returns(Settings);
            Probe.Setup(probe => probe.ProbeAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ObicoConnectivityResult(true, 0, null));
            string database = Guid.NewGuid().ToString();
            Provider = new ServiceCollection().AddSingleton(settings.Object).AddSingleton(Probe.Object)
                .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database)).BuildServiceProvider();
            Check = new(Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ObicoHealthCheck>.Instance, Clock);
        }

        public async Task AddServerAsync(string url, string? key, bool enabled = true)
        {
            using IServiceScope scope = Provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ObicoServers.Add(new ObicoServer { Name = "Private server name", Url = url, ApiKey = key, IsEnabled = enabled });
            await db.SaveChangesAsync();
        }

        public void Dispose() => Provider.Dispose();
    }
}
