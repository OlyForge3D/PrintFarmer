using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Health;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Health;

public sealed class PrintGuardHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Disabled_DoesNotCreateClient()
    {
        using var fixture = new Fixture();
        fixture.Settings.Enabled = false;

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["enabled"].Should().Be(false);
        fixture.Factory.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("", 5, 5, null)]
    [InlineData("not-a-url", 5, 5, null)]
    [InlineData("ftp://provider.invalid", 5, 5, null)]
    [InlineData("http://user:secret@provider.invalid", 5, 5, null)]
    [InlineData("http://provider.invalid?token=secret", 5, 5, null)]
    [InlineData("http://provider.invalid", 0, 5, null)]
    [InlineData("http://provider.invalid", 5, 61, null)]
    [InlineData("http://provider.invalid", 5, 5, "invalid token")]
    public async Task CheckHealthAsync_InvalidConfiguration_ReportsEnabledDegradedWithoutRequest(
        string url, int requestTimeout, int captureTimeout, string? token)
    {
        using var fixture = new Fixture();
        fixture.Settings.ApiUrl = url;
        fixture.Settings.RequestTimeoutSeconds = requestTimeout;
        fixture.Settings.CaptureTimeoutSeconds = captureTimeout;
        fixture.Settings.ApiToken = token;

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["enabled"].Should().Be(true);
        result.Description.Should().Contain("configuration invalid");
        JsonSerializer.Serialize(result).Should().NotContain("secret").And.NotContain("provider.invalid");
        fixture.Factory.Verify(factory => factory.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test-read-token")]
    public async Task CheckHealthAsync_ReadyHubAndReadAccess_UsesOnlyGetRoutesAndConfiguredToken(string? token)
    {
        using var fixture = new Fixture();
        fixture.Settings.ApiUrl = "http://provider.invalid/prefix/";
        fixture.Settings.ApiToken = token;
        fixture.Handler.Response = (request, _) =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.Content.Should().BeNull();
            (request.Headers.Authorization?.Parameter).Should().Be(token);
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/prefix/api/health"
                ? """{"ok":true,"version":"2.6.5"}""" : "[]"));
        };

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["enabled"].Should().Be(true);
        result.Description.Should().Contain("classification not tested");
        fixture.Handler.Paths.Should().Equal("/prefix/api/health", "/prefix/api/v1/monitors");
        JsonSerializer.Serialize(result).Should().NotContain("test-read-token");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task CheckHealthAsync_ReadApiRejected_DoesNotClaimHealthy(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/api/health"
                ? Json("""{"ok":true,"version":"2.6.5"}""")
                : Json("private upstream error", status));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["enabled"].Should().Be(true);
        result.Description.Should().Contain(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "read API access denied" : $"HTTP {(int)status}");
        JsonSerializer.Serialize(result).Should().NotContain("private");
    }

    [Theory]
    [InlineData("""{"ok":false,"version":"2.6.5"}""", "ready hub")]
    [InlineData("""{"ok":"true","version":"2.6.5"}""", "ready hub")]
    [InlineData("""{"ok":true}""", "ready hub")]
    [InlineData("""{"ok":true,"version":""}""", "ready hub")]
    [InlineData("[]", "ready hub")]
    [InlineData("<html>not PrintGuard</html>", "invalid JSON")]
    public async Task CheckHealthAsync_InvalidReadinessContract_DoesNotProbeReadApi(string body, string detail)
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => Task.FromResult(Json(body));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain(detail);
        fixture.Handler.Paths.Should().Equal("/api/health");
    }

    [Fact]
    public async Task CheckHealthAsync_UnexpectedReadApiContract_ReportsDegraded()
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => Task.FromResult(Json("""{"ok":true,"version":"2.6.5"}"""));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("unexpected response");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task CheckHealthAsync_ReadinessHttpFailure_ReportsStatusWithoutReadingBody(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => Task.FromResult(Json("private upstream details", status));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain($"HTTP {(int)status}");
        fixture.Handler.Paths.Should().Equal("/api/health");
        JsonSerializer.Serialize(result).Should().NotContain("private");
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionFailure_RedactsAndCachesFailure()
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => throw new HttpRequestException("secret URL and credentials");

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());
        await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().BeNull();
        result.Description.Should().Contain("connection failed");
        JsonSerializer.Serialize(result).Should().NotContain("secret").And.NotContain("credentials");
        fixture.Handler.Paths.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckHealthAsync_OversizedResponse_ReportsBoundedFailure()
    {
        using var fixture = new Fixture();
        fixture.Handler.Response = (_, _) => Task.FromResult(Json(new string('x', 65537)));

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("64 KB");
    }

    [Fact]
    public async Task CheckHealthAsync_StalledProvider_UsesConfiguredTimeout()
    {
        using var fixture = new Fixture();
        fixture.Settings.RequestTimeoutSeconds = 1;
        fixture.Handler.Response = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("[]");
        };

        HealthCheckResult result = await fixture.Check.CheckHealthAsync(new()).WaitAsync(TimeSpan.FromSeconds(3));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("timed out");
        PrintGuardHealthCheck.ProbeTimeout.Should().BeLessThan(TimeSpan.FromSeconds(8));
    }

#pragma warning disable IDISP013, VSTHRD003 // Deliberate concurrent callers; all tasks finish before fixture disposal.
    [Fact]
    public async Task CheckHealthAsync_ConcurrentAndCancelledCallers_ShareProbeAndRespectConfigurationChanges()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Response = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/health")
            {
                started.TrySetResult();
                await finish.Task.WaitAsync(ct);
                return Json("""{"ok":true,"version":"2.6.5"}""");
            }

            return Json("[]");
        };
        using var caller = new CancellationTokenSource();
        Task<HealthCheckResult> cancelled = fixture.Check.CheckHealthAsync(new(), caller.Token);
        await started.Task;
        Task<HealthCheckResult>[] concurrent = Enumerable.Range(0, 10)
            .Select(_ => fixture.Check.CheckHealthAsync(new())).ToArray();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        finish.SetResult();
        HealthCheckResult[] results = await Task.WhenAll(concurrent);
        results.Should().OnlyContain(result => result.Status == HealthStatus.Healthy);
        await fixture.Check.CheckHealthAsync(new());
        fixture.Handler.Paths.Should().HaveCount(2);

        fixture.Clock.Now += PrintGuardHealthCheck.CacheDuration;
        await fixture.Check.CheckHealthAsync(new());
        fixture.Handler.Paths.Should().HaveCount(4);
        fixture.Settings.ApiToken = "changed-read-token";
        await fixture.Check.CheckHealthAsync(new());
        fixture.Handler.Paths.Should().HaveCount(6);
        fixture.Settings.ApiUrl = "http://changed.invalid";
        await fixture.Check.CheckHealthAsync(new());
        fixture.Handler.Paths.Should().HaveCount(8);
        fixture.Settings.Enabled = false;
        (await fixture.Check.CheckHealthAsync(new())).Data["enabled"].Should().Be(false);
        fixture.Handler.Paths.Should().HaveCount(8);
    }
#pragma warning restore IDISP013, VSTHRD003

    [Fact]
    public void Registration_HealthServiceResolutions_UseOneSingleton()
    {
        using ServiceProvider provider = new ServiceCollection().AddLogging().AddOptions().AddHttpClient()
            .AddPrintFarmerHealthChecks().BuildServiceProvider();
        HealthCheckRegistration registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Single(value => value.Name == PrintGuardHealthCheck.Name);
        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();
        registration.Factory(first.ServiceProvider).Should().BeSameAs(registration.Factory(second.ServiceProvider));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response { get; set; } =
            (request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/api/health"
                ? """{"ok":true,"version":"2.6.5"}""" : "[]"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Response(request, cancellationToken);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public PrintGuardShadowSettings Settings { get; } = new() { Enabled = true, ApiUrl = "http://provider.invalid" };
        public Handler Handler { get; } = new();
        public Mock<IHttpClientFactory> Factory { get; } = new();
        public Clock Clock { get; } = new();
        public PrintGuardHealthCheck Check { get; }

        public Fixture()
        {
            var options = new Mock<IOptionsMonitor<PrintGuardShadowSettings>>();
            options.Setup(value => value.CurrentValue).Returns(() => Settings);
            Factory.Setup(factory => factory.CreateClient("PrintGuard"))
                .Returns(() => new HttpClient(Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan });
            Check = new(options.Object, Factory.Object, NullLogger<PrintGuardHealthCheck>.Instance, Clock);
        }

        public void Dispose() => Handler.Dispose();
    }
}
