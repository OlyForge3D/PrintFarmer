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
    private sealed class FakeProcessRunner(
        HostUpdateProcessResult containerInspectResult,
        HostUpdateProcessResult? imageInspectResult = null) : IHostUpdateProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
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
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, "api-1", "sha256:" + new string('b', 64));

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
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, "api-1", "sha256:" + new string('b', 64));

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHealthyAsync_ContainerInspectFails_ReturnsFalseAndNeverInspectsImage()
    {
        var runner = new FakeProcessRunner(new HostUpdateProcessResult(1, string.Empty, "no such container"));
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, "api-1", "sha256:" + new string('b', 64));

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
        var check = new DigestHostUpdateHealthCheck("digest:api", runner, "api-1", "sha256:" + new string('b', 64));

        bool result = await check.IsHealthyAsync(CancellationToken.None);

        result.Should().BeFalse();
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
