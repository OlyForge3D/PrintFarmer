using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>Activating N over a running, authenticated N-1 writer (issue #3126).</summary>
public sealed partial class HostUpdateCliOfflineActivateTests
{
    private const string MonolithRepository = "ghcr.io/olyforge3d/printfarmer-monolith";

    [HostStateFact]
    public async Task Activation_tolerates_running_authenticated_prior_writer_and_fences_it_before_changes()
    {
        StagePriorRecoverySet();
        await ImportAsync();
        await WritePriorInstalledStateAsync();
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + PriorAmd64Digests["monolith"];
        var http = new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true);

        JsonElement activated = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UsePriorWriterFence(services, runner, http)));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        runner.StoppedContainerIds.Should().Equal(["monolith-1"], "the fenced N-1 writer is stopped before backup");
        int stop = runner.Calls.FindIndex(call => call.Arguments is ["stop", ..]);
        int migrate = runner.Calls.FindIndex(call => call.Arguments.Contains("--host-update-migration"));
        stop.Should().BeGreaterThanOrEqualTo(0);
        (migrate < 0 || stop < migrate).Should().BeTrue("the writer is stopped before any migration runs");
        (await new FileInstalledHostStateStore(InstalledStatePath()).ReadAsync(CancellationToken.None))!
            .ReleaseId.Should().Be(TargetReleaseId);
        runner.ComposeUpCalls.Should().ContainSingle();
    }

    [HostStateFact]
    public async Task Activation_refuses_running_writer_when_prior_set_is_not_staged()
    {
        StagePriorRecoverySet();
        await WritePriorInstalledStateAsync();
        WriteRecord(Digest(_manifest));
        File.Delete(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorManifestName));
        File.Delete(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorSignatureName));
        await ImportAsync();
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + PriorAmd64Digests["monolith"];

        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UsePriorWriterFence(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        AssertRefused(refused, "writer_service_active:monolith:running");
        runner.ComposeUpCalls.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Activation_refuses_running_writer_when_prior_signature_does_not_verify()
    {
        StagePriorRecoverySet();
        await ImportAsync();
        await WritePriorInstalledStateAsync();
        _verifier.Reject = bytes => bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(_staging, HostUpdateOfflineRecovery.PriorManifestName)));
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + PriorAmd64Digests["monolith"];

        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UsePriorWriterFence(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        AssertRefused(refused, "writer_service_active:monolith:running");
        runner.ComposeUpCalls.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Activation_refuses_running_writer_when_installed_state_is_not_the_prior_set()
    {
        StagePriorRecoverySet();
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        await WriteInstalledStateAsync(request with { ReleaseId = "stable:1.2.2", ManifestDigest = "sha256:" + new string('9', 64) });
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + PriorAmd64Digests["monolith"];

        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UsePriorWriterFence(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        AssertRefused(refused, "writer_service_active:monolith:running");
        runner.ComposeUpCalls.Should().BeEmpty();
    }

    [HostStateTheory]
    [InlineData("child")]
    [InlineData("other-repository")]
    [InlineData("tag")]
    public async Task Activation_refuses_running_writer_not_on_the_installed_prior_pin(string variant)
    {
        StagePriorRecoverySet();
        await ImportAsync();
        await WritePriorInstalledStateAsync();
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = variant switch
        {
            "child" => MonolithRepository + "@" + PriorChildDigests["monolith"],
            "other-repository" => "ghcr.io/attacker/printfarmer-monolith@" + PriorAmd64Digests["monolith"],
            _ => MonolithRepository + ":latest",
        };

        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UsePriorWriterFence(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        AssertRefused(refused, "writer_service_active:monolith:running");
        runner.ComposeUpCalls.Should().BeEmpty();
    }

    [Fact]
    public void Writer_absence_probe_records_only_tolerated_running_writers()
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api"] = "api",
            ["monolith"] = "printfarmer",
        };
        string output = """
            {"Service":"api","State":"exited"}
            {"ID":"c1","Service":"printfarmer","State":"running","Image":"repo@sha256:1"}
            """;
        var tolerated = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        DockerComposeApiAbsenceProbe.ValidateComposeState(output, mappings, (_, image, id) => image == "repo@sha256:1" && id is not null, tolerated)
            .Should().BeNull();
        tolerated.Should().ContainKey("monolith").WhoseValue.Should().Equal("c1 repo@sha256:1");

        DockerComposeApiAbsenceProbe.ValidateComposeState(output, mappings, (_, image, _) => image == "repo@sha256:2", null)
            .Should().Be("writer_service_active:monolith:running");
        DockerComposeApiAbsenceProbe.ValidateComposeState("""{"ID":"c1","Service":"printfarmer","State":"running"}""", mappings, (_, image, _) => image is not null, null)
            .Should().Be("writer_service_active:monolith:running", "a running writer with no observable image is never tolerated");
    }

    [Theory]
    [InlineData("created")]
    [InlineData("restarting")]
    [InlineData("paused")]
    public void Writer_absence_probe_never_tolerates_a_writer_that_is_not_steadily_running(string state)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal) { ["monolith"] = "printfarmer" };
        string output = $$"""{"ID":"c1","Service":"printfarmer","State":"{{state}}","Image":"repo@sha256:1"}""";

        DockerComposeApiAbsenceProbe.ValidateComposeState(output, mappings, (_, _, _) => true, null)
            .Should().Be("writer_service_active:monolith:" + state, "only a running container can be fenced and re-identified");
    }

    [HostStateTheory]
    [InlineData("running", true)]
    [InlineData("restarting", false)]
    public async Task Activation_tolerates_prior_writer_only_while_it_is_running(string state, bool tolerated)
    {
        StagePriorRecoverySet();
        await ImportAsync();
        await WritePriorInstalledStateAsync();
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + PriorAmd64Digests["monolith"];
        runner.RunningWriterStates["monolith"] = state;

        JsonElement envelope = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UsePriorWriterFence(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        if (tolerated)
        {
            envelope.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, envelope.ToString());
        }
        else
        {
            AssertRefused(envelope, "writer_service_active:monolith:restarting");
            runner.ComposeUpCalls.Should().BeEmpty();
        }
    }

    [Fact]
    public void Writer_absence_probe_refuses_a_running_writer_without_a_container_id()
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal) { ["monolith"] = "printfarmer" };

        DockerComposeApiAbsenceProbe.ValidateComposeState(
                """{"Service":"printfarmer","State":"running","Image":"repo@sha256:1"}""",
                mappings,
                (_, image, id) => image is not null && id is not null,
                null)
            .Should().Be("writer_service_active:monolith:running");
    }

    [Theory]
    [InlineData("tolerated", 0, true)]
    [InlineData("tolerated", 1, false)]
    [InlineData("new-writer", 0, false)]
    [InlineData("re-imaged", 0, false)]
    [InlineData("replaced-container", 0, false)]
    [InlineData("extra-replica", 0, false)]
    [InlineData("restarting", 0, false)]
    [InlineData("stop-fails", 0, false)]
    public async Task Prior_release_writer_fence_proves_only_closed_drained_and_unchanged_writers(string scenario, int activeWork, bool expected)
    {
        string pin = MonolithRepository + "@" + PriorAmd64Digests["monolith"];
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = scenario == "re-imaged" ? MonolithRepository + "@" + PriorChildDigests["monolith"] : pin;
        if (scenario == "new-writer")
        {
            runner.RunningWriterImages["api"] = "ghcr.io/olyforge3d/printfarmer-api@" + PriorAmd64Digests["api"];
        }

        if (scenario == "replaced-container")
        {
            runner.RunningWriterContainerIds["monolith"] = ["monolith-2"];
        }

        if (scenario == "extra-replica")
        {
            runner.RunningWriterContainerIds["monolith"] = ["monolith-1", "monolith-2"];
        }

        if (scenario == "restarting")
        {
            runner.RunningWriterStates["monolith"] = "restarting";
        }

        runner.FailContainerStop = scenario == "stop-fails";

        (HostUpdatePriorReleaseWriterFence fence, InMemoryHostUpdateAdmissionGate gate, ServiceProvider provider) =
            CreatePriorWriterFence(runner, activeWork, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["monolith"] = ["monolith-1 " + pin] });
        await using (provider)
        {
            (await fence.IsQuiescedAsync(CancellationToken.None)).Should().BeFalse("admission is still open");
            await fence.QuiesceAsync(CancellationToken.None);
            (await gate.IsClosedAsync(CancellationToken.None)).Should().BeTrue();
            (await fence.IsQuiescedAsync(CancellationToken.None)).Should().Be(expected);
            if (scenario == "tolerated" && activeWork == 0)
            {
                runner.StoppedContainerIds.Should().Equal(["monolith-1"], "only the proven tolerated container is stopped, by ID");
                (await fence.IsQuiescedAsync(CancellationToken.None)).Should().BeTrue("a stopped tolerated writer stays proven");
            }
            else if (scenario != "stop-fails")
            {
                runner.StoppedContainerIds.Should().BeEmpty("an unproven writer set is never stopped");
            }
        }
    }

    [Fact]
    public async Task Prior_release_writer_fence_is_inert_when_no_writer_was_tolerated()
    {
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        (HostUpdatePriorReleaseWriterFence fence, InMemoryHostUpdateAdmissionGate gate, ServiceProvider provider) =
            CreatePriorWriterFence(runner, activeWork: 5, new Dictionary<string, string[]>(StringComparer.Ordinal));
        await using (provider)
        {
            await fence.QuiesceAsync(CancellationToken.None);
            (await gate.IsClosedAsync(CancellationToken.None)).Should().BeFalse();
            (await fence.IsQuiescedAsync(CancellationToken.None)).Should().BeTrue();
            runner.Calls.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Strict_writer_host_proof_never_counts_a_tolerated_writer_as_stopped()
    {
        string pin = MonolithRepository + "@" + PriorAmd64Digests["monolith"];
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = pin;
        var context = new HostUpdatePriorReleaseContext();
        context.RecordTolerated(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["monolith"] = ["monolith-1 " + pin] });
        var options = new HostUpdateExecutionOptions { ActiveServiceIds = ["api", "frontend", "slicer-host", "monolith"] };
        var probe = new DockerComposeApiAbsenceProbe(runner, new DockerOnlyResolver(), options, context);

        (await probe.ValidateWriterHostsStoppedAsync(CancellationToken.None))
            .Should().Be("writer_service_active:monolith:running", "the #3127 writer-host proof must see the N-1 writer running");
        context.ToleratedWriters.Should().ContainKey("monolith", "the strict proof never rewrites the tolerated set");
        runner.StoppedContainerIds.Should().BeEmpty();
    }

    private static (HostUpdatePriorReleaseWriterFence Fence, InMemoryHostUpdateAdmissionGate Gate, ServiceProvider Provider) CreatePriorWriterFence(
        IntegratedActivationProcessRunner runner,
        int activeWork,
        Dictionary<string, string[]> tolerated)
    {
        var context = new HostUpdatePriorReleaseContext();
        context.RecordTolerated(tolerated);
        var gate = new InMemoryHostUpdateAdmissionGate();
        ServiceProvider provider = new ServiceCollection()
            .AddScoped<IActiveWorkObservationPort>(_ => new FixedActiveWorkObservationPort(activeWork))
            .BuildServiceProvider();
        var options = new HostUpdateExecutionOptions { ActiveServiceIds = ["api", "frontend", "slicer-host", "monolith"] };
        var probe = new DockerComposeApiAbsenceProbe(runner, new DockerOnlyResolver(), options, context);
        var fence = new HostUpdatePriorReleaseWriterFence(context, gate, provider.GetRequiredService<IServiceScopeFactory>(), probe);
        return (fence, gate, provider);
    }

    private static void UsePriorWriterFence(IServiceCollection services, IntegratedActivationProcessRunner runner, RecordingHealthHttpClientFactory http) =>
        UseIntegratedBoundaries(services, runner, http);

    private sealed class FixedActiveWorkObservationPort(int count) : IActiveWorkObservationPort
    {
        public Task<int> CountActiveAsync(CancellationToken cancellationToken) => Task.FromResult(count);
    }

    private sealed class DockerOnlyResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => "/usr/bin/" + toolName;
    }
}
