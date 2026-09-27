using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// Offline fence and in-network health contract for the host-local CLI (issue #3127): background
/// writers are proven fenced by a closed durable gate plus stopped writer hosts, and the aggregate
/// <c>/health</c> report is read inside the compose network with the unchanged readiness rule.
/// </summary>
public sealed class HostUpdateCliOfflineContractTests
{
    private const string HealthyReport = """{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}""";

    private static readonly string[] BackgroundWriterNames =
    [
        "queue-outbox-publisher",
        "power-reading-prune",
        "queue-retention-prune",
        "backend-start-command-consumer",
        "backend-control-command-consumer",
        "bed-clear-acknowledgement-expiry",
        "auto-dispatch",
        "webhook-delivery",
        "queue-reconciliation",
    ];

    private static readonly HostUpdateExecutionRequest Request = new(
        "stable:1.2.3",
        1,
        "sha256:" + new string('a', 64),
        new string('c', 40),
        HostUpdateExecutionChannel.Stable,
        []);

    [Fact]
    public async Task Fence_proves_every_writer_when_the_gate_is_closed_and_writer_hosts_are_stopped()
    {
        var gate = new MemoryGate();
        var probe = new ScriptedProbe(_ => null);

        await CreateCoordinator(gate, probe).RunAsync(Request, CancellationToken.None);

        gate.Closed.Should().BeTrue("the durable fence stays closed until verified release");
        probe.Calls.Should().BeGreaterThan(0, "every background writer is proven by a fresh probe, not assumed");
        probe.Requests.Should().OnlyContain(request => ReferenceEquals(request, Request));
    }

    [Fact]
    public async Task Fence_fails_closed_listing_every_background_writer_while_a_writer_host_is_running()
    {
        var gate = new MemoryGate();
        var probe = new ScriptedProbe(_ => "writer_absence_unproven:api_running:api");

        Func<Task> run = () => CreateCoordinator(gate, probe).RunAsync(Request, CancellationToken.None);

        HostUpdateFenceProofFailedException failure = (await run.Should().ThrowAsync<HostUpdateFenceProofFailedException>()).Which;
        failure.UnfencedWriterNames.Should().BeEquivalentTo(BackgroundWriterNames);
        failure.UnfencedWriterNames.Should().NotContain("api-admission", "the admission gate itself was proven closed");
        gate.Closed.Should().BeTrue("a failed fence never reopens admission");
        probe.Calls.Should().BeGreaterThan(BackgroundWriterNames.Length, "the probe is re-run on every poll until the deadline");
    }

    [Fact]
    public async Task Fence_accepts_a_writer_host_that_stops_within_the_bounded_proof_window()
    {
        var gate = new MemoryGate();
        var probe = new ScriptedProbe(call => call <= BackgroundWriterNames.Length ? "writer_absence_unproven:api_running:api" : null);

        await CreateCoordinator(gate, probe).RunAsync(Request, CancellationToken.None);

        probe.Calls.Should().BeGreaterThan(BackgroundWriterNames.Length);
    }

    [Fact]
    public async Task Fence_never_proves_background_writers_while_the_durable_gate_is_open()
    {
        var gate = new MemoryGate { IgnoreClose = true };
        var probe = new ScriptedProbe(_ => null);

        Func<Task> run = () => CreateCoordinator(gate, probe).RunAsync(Request, CancellationToken.None);

        HostUpdateFenceProofFailedException failure = (await run.Should().ThrowAsync<HostUpdateFenceProofFailedException>()).Which;
        failure.UnfencedWriterNames.Should().Contain("api-admission").And.Contain(BackgroundWriterNames);
        probe.Calls.Should().Be(0, "an open gate is disqualifying before any host is probed");
    }

    [Fact]
    public async Task Fence_fails_closed_when_the_topology_runs_no_background_writer_host()
    {
        var gate = new MemoryGate();
        var probe = new ScriptedProbe(_ => null);
        HostUpdateExecutionOptions options = Options();
        options.ActiveServiceIds = ["frontend", "slicer-host"];

        Func<Task> run = () => CreateCoordinator(gate, probe, options).RunAsync(Request, CancellationToken.None);

        HostUpdateFenceProofFailedException failure = (await run.Should().ThrowAsync<HostUpdateFenceProofFailedException>()).Which;
        failure.UnfencedWriterNames.Should().BeEquivalentTo(BackgroundWriterNames);
    }

    [Fact]
    public async Task Fence_reports_a_configured_required_writer_it_cannot_prove_as_unfenced()
    {
        var gate = new MemoryGate();
        var probe = new ScriptedProbe(_ => null);
        HostUpdateExecutionOptions options = Options();
        options.RequiredFencedWriterNames = [.. options.RequiredFencedWriterNames, "deployment-specific-writer"];

        Func<Task> run = () => CreateCoordinator(gate, probe, options).RunAsync(Request, CancellationToken.None);

        HostUpdateFenceProofFailedException failure = (await run.Should().ThrowAsync<HostUpdateFenceProofFailedException>()).Which;
        failure.UnfencedWriterNames.Should().Equal("deployment-specific-writer");
    }

    [Fact]
    public async Task Fence_release_reopens_the_durable_admission_gate()
    {
        var gate = new MemoryGate();
        HostUpdateCliWriterStoppedFenceCoordinator coordinator = CreateCoordinator(gate, new ScriptedProbe(_ => null));
        await coordinator.RunAsync(Request, CancellationToken.None);

        await coordinator.ReleaseAsync(CancellationToken.None);

        gate.Closed.Should().BeFalse();
    }

    [Theory]
    [InlineData("split", "api", "5245")]
    [InlineData("monolith", "printfarmer", "5000")]
    public async Task Health_runs_curl_inside_the_api_container_against_its_own_loopback_listener(string topology, string composeService, string port)
    {
        HostUpdateExecutionOptions options = Options();
        if (topology == "monolith")
        {
            options.ActiveServiceIds = ["monolith"];
        }

        var runner = new ScriptedRunner(new HostUpdateProcessResult(0, HealthyReport, string.Empty));

        bool healthy = await new ComposeExecAggregateHealthCheck(runner, new Resolver(), options).IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeTrue();
        runner.FileName.Should().Be("/usr/bin/docker");
        List<string> expected = ["compose"];
        foreach (string file in options.ComposeFiles)
        {
            expected.AddRange(["-f", file]);
        }

        expected.AddRange(
        [
            "-p", options.ComposeProjectName, "exec", "-T", composeService,
            "curl", "--silent", "--show-error", "--noproxy", "*", "--max-time", "15",
            $"http://127.0.0.1:{port}/health",
        ]);
        runner.Arguments.Should().Equal(expected);
        runner.Timeout.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData("""{"status":"Degraded","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}""")]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"Healthy"},"signalr":{"status":"Healthy"}}}""")]
    [InlineData("""{"status":"Healthy","results":{"comprehensive":{"status":"Unhealthy"},"signalr":{"status":"Healthy"},"spoolman":{"status":"Healthy"}}}""")]
    [InlineData("""{"status":"Healthy"}""")]
    [InlineData("curl: (7) Failed to connect")]
    [InlineData("")]
    public async Task Health_applies_the_unchanged_aggregate_readiness_rule(string body)
    {
        var runner = new ScriptedRunner(new HostUpdateProcessResult(0, body, string.Empty));

        bool healthy = await new ComposeExecAggregateHealthCheck(runner, new Resolver(), Options()).IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeFalse();
    }

    [Fact]
    public async Task Health_is_unhealthy_when_the_exec_fails_even_with_a_healthy_looking_body()
    {
        var runner = new ScriptedRunner(new HostUpdateProcessResult(1, HealthyReport, "service \"api\" is not running"));

        bool healthy = await new ComposeExecAggregateHealthCheck(runner, new Resolver(), Options()).IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    public async Task Health_is_unhealthy_when_the_process_cannot_complete(Type exceptionType)
    {
        var runner = new ScriptedRunner(new HostUpdateProcessResult(0, HealthyReport, string.Empty))
        {
            Throw = (Exception)Activator.CreateInstance(exceptionType)!,
        };

        bool healthy = await new ComposeExecAggregateHealthCheck(runner, new Resolver(), Options()).IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeFalse();
    }

    [Fact]
    public async Task Health_fails_closed_without_starting_a_process_when_no_api_host_is_mapped()
    {
        HostUpdateExecutionOptions options = Options();
        options.ActiveServiceIds = ["frontend"];
        var runner = new ScriptedRunner(new HostUpdateProcessResult(0, HealthyReport, string.Empty));

        bool healthy = await new ComposeExecAggregateHealthCheck(runner, new Resolver(), options).IsHealthyAsync(CancellationToken.None);

        healthy.Should().BeFalse();
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public void Cli_service_graph_replaces_both_host_process_bound_boundaries()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        IServiceCollection services = HostUpdateCli.ConfigureServices(new ServiceCollection(), configuration, TextWriter.Null);

        services.Where(d => d.ServiceType == typeof(IHostUpdateFenceCoordinator)).Should().ContainSingle();
        services.Where(d => d.ServiceType == typeof(IHostUpdateAggregateHealthCheck)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<ComposeExecAggregateHealthCheck>();
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHostUpdateFenceCoordinator>().Should().BeOfType<HostUpdateCliWriterStoppedFenceCoordinator>();
        provider.GetRequiredService<IHostUpdateAggregateHealthCheck>().Name
            .Should().Be(HostUpdateRecoveryEngineRegistration.AggregateHealthCheckName);
    }

    private static HostUpdateExecutionOptions Options() => new()
    {
        FenceProofTimeoutSeconds = 1,
        FencePollIntervalSeconds = 1,
    };

    private static HostUpdateCliWriterStoppedFenceCoordinator CreateCoordinator(
        MemoryGate gate,
        ScriptedProbe probe,
        HostUpdateExecutionOptions? options = null)
    {
        IFenceableWriter[] writers =
        [
            new AdmissionFenceableWriter(gate),
            .. BackgroundWriterNames.Select(name => (IFenceableWriter)new BackgroundWriterFenceableWriter(name, new InMemoryHostUpdateWriterActivityFlag())),
        ];
        return new HostUpdateCliWriterStoppedFenceCoordinator(gate, probe, writers, options ?? Options(), NullLoggerFactory.Instance);
    }

    private sealed class MemoryGate : IHostUpdateAdmissionGate
    {
        public bool Closed { get; private set; }

        public bool IgnoreClose { get; init; }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            Closed = !IgnoreClose;
            return Task.CompletedTask;
        }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            Closed = false;
            return Task.CompletedTask;
        }

        public Task<bool> IsClosedAsync(CancellationToken cancellationToken) => Task.FromResult(Closed);
    }

    private sealed class ScriptedProbe(Func<int, string?> result) : IHostUpdateOfflineActivationSafetyProbe
    {
        public int Calls { get; private set; }

        public List<HostUpdateExecutionRequest> Requests { get; } = [];

        public Task<string?> ValidateSafeToExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result(++Calls));
        }
    }

    private sealed class Resolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => "/usr/bin/" + toolName;
    }

    private sealed class ScriptedRunner(HostUpdateProcessResult result) : IHostUpdateProcessRunner
    {
        public int Calls { get; private set; }

        public string? FileName { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public TimeSpan Timeout { get; private set; }

        public Exception? Throw { get; init; }

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls++;
            FileName = fileName;
            Arguments = [.. arguments];
            Timeout = timeout;
            return Throw is null ? Task.FromResult(result) : Task.FromException<HostUpdateProcessResult>(Throw);
        }
    }
}
