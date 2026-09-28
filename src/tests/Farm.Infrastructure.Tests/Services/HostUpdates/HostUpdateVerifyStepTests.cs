using System.Net;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Kane audit P0.5 / Bishop+Hicks review: the executor's verify step must require the exact
/// aggregated <c>/health</c> report status ("Healthy"), never merely an HTTP 200 -- ASP.NET
/// Core's default health check middleware also returns 200 for "Degraded", and the previously
/// wired <c>/healthz</c> liveness probe is a hardcoded stub that always returns 200
/// unconditionally. The real endpoint is serialized with
/// <c>Program.HealthJsonOptions</c> (<c>PropertyNamingPolicy = JsonNamingPolicy.CamelCase</c>),
/// so the wire property is <c>status</c>, not <c>Status</c> -- earlier tests here used a
/// hand-built PascalCase fixture that happened to match the (buggy) PascalCase-only lookup,
/// hiding the mismatch. <see cref="IsHealthyAsync_RealCamelCaseSerializedFixture_ReturnsTrue"/>
/// and <see cref="IsHealthyAsync_RealCamelCaseSerializedDegradedFixture_ReturnsFalse"/> below use
/// the actual lowercase-property shape the endpoint really emits.
/// </summary>
public sealed class AggregateHostUpdateHealthCheckTests
{
    private static AggregateHostUpdateHealthCheck CreateCheck(HttpStatusCode statusCode, string body)
    {
        var handler = new StubHttpMessageHandler(statusCode, body);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new AggregateHostUpdateHealthCheck("api-comprehensive-health", client, "/health");
    }

    [Fact]
    public async Task IsHealthyAsync_ExactlyHealthyStatus_ReturnsTrue()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, """{"Status":"Healthy","Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHealthyAsync_RealCamelCaseSerializedFixture_ReturnsTrue()
    {
        // This is the actual shape ProgramHelpers.WriteHealthResponseAsync produces via
        // Program.HealthJsonOptions (PropertyNamingPolicy = JsonNamingPolicy.CamelCase): the
        // top-level "status" property key is lowercase even though the enum value itself
        // ("Healthy") is not renamed by a naming policy.
        AggregateHostUpdateHealthCheck check = CreateCheck(
            HttpStatusCode.OK,
            """{"status":"Healthy","totalChecksDuration":"00:00:00.0120000","results":{"comprehensive":{"status":"Healthy","duration":"00:00:00.0050000"}}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHealthyAsync_RealCamelCaseSerializedDegradedFixture_ReturnsFalse()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(
            HttpStatusCode.OK,
            """{"status":"Degraded","totalChecksDuration":"00:00:00.0120000","results":{"spoolman":{"status":"Degraded","duration":"00:00:00.0050000"}}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_DegradedStatusWithHttp200_ReturnsFalse()
    {
        // ASP.NET Core's default health check middleware maps both Healthy and Degraded to HTTP 200;
        // a naive "response.IsSuccessStatusCode" check would incorrectly treat this as healthy.
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, """{"Status":"Degraded","Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_UnhealthyStatusWithHttp503_ReturnsFalse()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.ServiceUnavailable, """{"Status":"Unhealthy","Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_MalformedJsonBody_ReturnsFalseRatherThanThrowing()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, "not json");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_MissingStatusProperty_ReturnsFalse()
    {
        AggregateHostUpdateHealthCheck check = CreateCheck(HttpStatusCode.OK, """{"Results":{}}""");

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }


    [Fact]
    public async Task IsHealthyAsync_RequiredResultMissing_ReturnsFalse()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"}}}""");
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var check = new AggregateHostUpdateHealthCheck(
            "api-comprehensive-health",
            client,
            "/health",
            new HashSet<string>(StringComparer.Ordinal) { "comprehensive", "signalr" });

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_RequiredResultDegraded_ReturnsFalse()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Degraded"}}}""");
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var check = new AggregateHostUpdateHealthCheck(
            "api-comprehensive-health",
            client,
            "/health",
            new HashSet<string>(StringComparer.Ordinal) { "comprehensive", "signalr" });

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }
    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            };
            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Bishop/Hicks review (issue #2663): <see cref="DigestHostUpdateHealthCheck"/> previously ran a
/// single <c>docker inspect --format {{index .RepoDigests 0}} &lt;container&gt;</c>, but
/// <c>.RepoDigests</c> does not exist on <c>docker container inspect</c> output at all -- only on
/// <c>docker image inspect</c>. These tests exercise the fixed two-step probe: (1) container
/// inspect resolves the running image reference via <c>.Image</c>; (2) that reference is fed to
/// image inspect to resolve the real repo digest. A fake <see cref="IHostUpdateProcessRunner"/>
/// asserts both commands are issued with the exact expected arguments, in order.
/// </summary>
public sealed class DigestHostUpdateHealthCheckTests
{
    private sealed class TestExecutableResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => Path.Combine(Path.GetTempPath(), toolName + ".exe");
    }

    private sealed class FakeProcessRunner(
        HostUpdateProcessResult containerInspectResult,
        HostUpdateProcessResult? imageInspectResult = null) : IHostUpdateProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<string> FileNames { get; } = [];

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            FileNames.Add(fileName);
            Calls.Add(arguments);
            bool isContainerInspect = arguments.Count > 0 && string.Equals(arguments[0], "container", StringComparison.Ordinal);
            return Task.FromResult(isContainerInspect ? containerInspectResult : (imageInspectResult ?? containerInspectResult));
        }
    }

    [Fact]
    public async Task IsHealthyAsync_ContainerImageResolvesToMatchingRepoDigest_ReturnsTrue()
    {
        var runner = new FakeProcessRunner(
            new HostUpdateProcessResult(0, "sha256:" + new string('a', 64), string.Empty),
            new HostUpdateProcessResult(0, "example.com/api@sha256:" + new string('b', 64), string.Empty));
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, new TestExecutableResolver(), "api-1", "sha256:" + new string('b', 64));

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeTrue();
        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Should().ContainInOrder("container", "inspect", "--format", "{{.Image}}", "api-1");
        runner.Calls[1].Should().ContainInOrder("image", "inspect", "--format", "{{index .RepoDigests 0}}", "sha256:" + new string('a', 64));
    }

    [Fact]
    public async Task IsHealthyAsync_RepoDigestDoesNotMatchExpected_ReturnsFalse()
    {
        var runner = new FakeProcessRunner(
            new HostUpdateProcessResult(0, "sha256:" + new string('a', 64), string.Empty),
            new HostUpdateProcessResult(0, "example.com/api@sha256:" + new string('c', 64), string.Empty));
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, new TestExecutableResolver(), "api-1", "sha256:" + new string('b', 64));

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_ContainerInspectFails_ReturnsFalseAndNeverInspectsImage()
    {
        var runner = new FakeProcessRunner(new HostUpdateProcessResult(1, string.Empty, "no such container"));
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, new TestExecutableResolver(), "api-1", "sha256:" + new string('b', 64));

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task IsHealthyAsync_ImageInspectFails_ReturnsFalse()
    {
        var runner = new FakeProcessRunner(
            new HostUpdateProcessResult(0, "sha256:" + new string('a', 64), string.Empty),
            new HostUpdateProcessResult(1, string.Empty, "no such image"));
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, new TestExecutableResolver(), "api-1", "sha256:" + new string('b', 64));

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_UsesConfiguredAbsoluteDockerPathThroughConstrainedRunner()
    {
        var inner = new FakeProcessRunner(
            new HostUpdateProcessResult(0, "sha256:" + new string('a', 64), string.Empty),
            new HostUpdateProcessResult(0, "example.com/api@sha256:" + new string('b', 64), string.Empty));
        string dockerPath = Path.Combine(Path.GetTempPath(), "docker.exe");
        var runner = new ConstrainedHostUpdateProcessRunner(
            inner,
            new HashSet<string>(StringComparer.Ordinal) { dockerPath });
        var check = new DigestHostUpdateHealthCheck(
            "digest:api",
            runner,
            new TestExecutableResolver(),
            "api-1",
            "sha256:" + new string('b', 64));

        Assert.True(await check.IsHealthyAsync(CancellationToken.None));
        inner.Calls.Should().HaveCount(2);
        inner.FileNames.Should().OnlyContain(fileName => fileName == dockerPath);
    }
}
public sealed class HostUpdateHealthVerifierTests
{
    [Fact]
    public async Task VerifyDigestsAsync_PartialServiceSet_ThrowsBeforeHealthChecks()
    {
        var check = new RecordingHealthCheck();
        var verifier = new HostUpdateHealthVerifier(
            [check],
            (serviceId, digest) => new RecordingHealthCheck(),
            TimeSpan.Zero,
            TimeSpan.Zero,
            new HashSet<string>(StringComparer.Ordinal) { "api", "frontend" });

        Func<Task> act = () => verifier.VerifyDigestsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["api"] = "sha256:" + new string('a', 64) },
            CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateVerificationTargetSetException>();
        check.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task VerifyDigestsAsync_InactiveSignedTargets_AreNotObserved()
    {
        var observed = new List<string>();
        var verifier = new HostUpdateHealthVerifier(
            [],
            (serviceId, digest) =>
            {
                observed.Add(serviceId);
                return new RecordingHealthCheck();
            },
            TimeSpan.Zero,
            TimeSpan.Zero,
            new HashSet<string>(StringComparer.Ordinal) { "monolith" });

        await verifier.VerifyDigestsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["api"] = "sha256:" + new string('a', 64),
                ["monolith"] = "sha256:" + new string('b', 64),
            },
            CancellationToken.None);

        observed.Should().Equal("monolith");
    }

    [Fact]
    public async Task VerifyDigestsAsync_EmptyDigestMap_ThrowsBeforeHealthChecks()
    {
        var check = new RecordingHealthCheck();
        var verifier = new HostUpdateHealthVerifier(
            [check],
            (serviceId, digest) => new RecordingHealthCheck(),
            TimeSpan.Zero,
            TimeSpan.Zero);

        Func<Task> act = () => verifier.VerifyDigestsAsync(new Dictionary<string, string>(StringComparer.Ordinal), CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateVerificationTargetSetException>();
        check.CallCount.Should().Be(0);
    }

    private sealed class RecordingHealthCheck : IHostUpdateHealthCheck
    {
        public string Name => "recording";

        public int CallCount { get; private set; }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Transport-independent aggregate readiness rule shared by the API's HTTP check and the offline
/// CLI's in-network check (issue #3127).
/// </summary>
public sealed class HostUpdateAggregateHealthReportTests
{
    private static readonly IReadOnlySet<string> Required = new HashSet<string>(StringComparer.Ordinal) { "comprehensive", "signalr" };

    [Theory]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"}}}""", true)]
    [InlineData("""{"status":"Degraded","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"Degraded"},"signalr":{"status":"Healthy"}}}""", false)]
    [InlineData("""{"status":"Healthy","results":[]}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":"Healthy","signalr":{"status":"Healthy"}}}""", false)]
    [InlineData("""{"status":1,"results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"}}}""", false)]
    // Issue #3145: the real writer emits each entry status as the numeric HealthStatus enum.
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":2},"signalr":{"status":2}}}""", true)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":2},"signalr":{"status":"Healthy"}}}""", true)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":2},"signalr":{"status":1}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":0},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"Unhealthy"},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":3},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":-1},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":2.5},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"2"},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":true},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":null},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"description":"no status"},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":99999999999},"signalr":{"status":2}}}""", false)]
    [InlineData("""{"status":2,"results":{"comprehensive":{"status":2},"signalr":{"status":2}}}""", true)]
    [InlineData("""{"status":0,"results":{"comprehensive":{"status":2},"signalr":{"status":2}}}""", false)]
    [InlineData("""["Healthy"]""", false)]
    [InlineData("curl: (7) Failed to connect to 127.0.0.1 port 5245", false)]
    [InlineData("", false)]
    public void IsHealthy_RequiresHealthyTopLevelAndEveryRequiredResult(string body, bool expected) =>
        HostUpdateAggregateHealthReport.IsHealthy(body, Required).Should().Be(expected);

    [Fact]
    public void IsHealthy_NullBody_IsUnhealthy() =>
        HostUpdateAggregateHealthReport.IsHealthy(null, Required).Should().BeFalse();
}

/// <summary>Issue #3145: failed check names are surfaced in CLI output, so they are redacted and bounded.</summary>
public sealed class HostUpdateVerificationTimeoutExceptionTests
{
    [Fact]
    public void RedactedFailedCheckNames_KeepsSafeNamesUnchanged()
    {
        var exception = new HostUpdateVerificationTimeoutException(["aggregate-health", "digest:api", "nginx.tls_ready"]);

        exception.RedactedFailedCheckNames().Should().Equal("aggregate-health", "digest:api", "nginx.tls_ready");
    }

    [Fact]
    public void RedactedFailedCheckNames_ReplacesUnsafeCharactersAndTruncates()
    {
        var exception = new HostUpdateVerificationTimeoutException(["http://user:secret@host/x?y=1 z\n", new string('a', 200)]);

        IReadOnlyList<string> redacted = exception.RedactedFailedCheckNames();

        redacted[0].Should().Be("http:__user:secret_host_x_y_1_z_");
        redacted[0].Should().NotContain("@").And.NotContain("/").And.NotContain("\n");
        redacted[1].Should().Be(new string('a', 64));
    }

    [Fact]
    public void RedactedFailedCheckNames_BoundsTheNumberOfNames()
    {
        var exception = new HostUpdateVerificationTimeoutException([.. Enumerable.Range(0, 20).Select(i => $"check-{i}")]);

        IReadOnlyList<string> redacted = exception.RedactedFailedCheckNames();

        redacted.Should().HaveCount(17);
        redacted.Take(16).Should().Equal(Enumerable.Range(0, 16).Select(i => $"check-{i}"));
        redacted[16].Should().Be("+4_more");
    }
}
