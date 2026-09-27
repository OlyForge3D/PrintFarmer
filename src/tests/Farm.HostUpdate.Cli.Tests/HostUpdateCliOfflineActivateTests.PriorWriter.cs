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
            {"Service":"printfarmer","State":"running","Image":"repo@sha256:1"}
            """;
        var tolerated = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        DockerComposeApiAbsenceProbe.ValidateComposeState(output, mappings, (_, image) => image == "repo@sha256:1", tolerated)
            .Should().BeNull();
        tolerated.Should().ContainKey("monolith").WhoseValue.Should().Equal("repo@sha256:1");

        DockerComposeApiAbsenceProbe.ValidateComposeState(output, mappings, (_, image) => image == "repo@sha256:2", null)
            .Should().Be("writer_service_active:monolith:running");
        DockerComposeApiAbsenceProbe.ValidateComposeState("""{"Service":"printfarmer","State":"running"}""", mappings, (_, image) => image is not null, null)
            .Should().Be("writer_service_active:monolith:running", "a running writer with no observable image is never tolerated");
    }

    [Theory]
    [InlineData("tolerated", 0, true)]
    [InlineData("tolerated", 1, false)]
    [InlineData("new-writer", 0, false)]
    [InlineData("re-imaged", 0, false)]
    public async Task Prior_release_writer_fence_proves_only_closed_drained_and_unchanged_writers(string scenario, int activeWork, bool expected)
    {
        string pin = MonolithRepository + "@" + PriorAmd64Digests["monolith"];
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = scenario == "re-imaged" ? MonolithRepository + "@" + PriorChildDigests["monolith"] : pin;
        if (scenario == "new-writer")
        {
            runner.RunningWriterImages["api"] = "ghcr.io/olyforge3d/printfarmer-api@" + PriorAmd64Digests["api"];
        }

        (HostUpdatePriorReleaseWriterFence fence, InMemoryHostUpdateAdmissionGate gate, ServiceProvider provider) =
            CreatePriorWriterFence(runner, activeWork, new Dictionary<string, string[]>(StringComparer.Ordinal) { ["monolith"] = [pin] });
        await using (provider)
        {
            (await fence.IsQuiescedAsync(CancellationToken.None)).Should().BeFalse("admission is still open");
            await fence.QuiesceAsync(CancellationToken.None);
            (await gate.IsClosedAsync(CancellationToken.None)).Should().BeTrue();
            (await fence.IsQuiescedAsync(CancellationToken.None)).Should().Be(expected);
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

    private static void UsePriorWriterFence(IServiceCollection services, IntegratedActivationProcessRunner runner, RecordingHealthHttpClientFactory http)
    {
        UseIntegratedBoundaries(services, runner, http);
        services.RemoveAll<IReadOnlyList<IFenceableWriter>>();
        services.AddSingleton<IReadOnlyList<IFenceableWriter>>(sp => [ActivatorUtilities.CreateInstance<HostUpdatePriorReleaseWriterFence>(sp)]);
    }

    private sealed class FixedActiveWorkObservationPort(int count) : IActiveWorkObservationPort
    {
        public Task<int> CountActiveAsync(CancellationToken cancellationToken) => Task.FromResult(count);
    }

    private sealed class DockerOnlyResolver : IHostUpdateExecutableResolver
    {
        public string Resolve(string toolName) => "/usr/bin/" + toolName;
    }
}
