using System.Text.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>Redriving an activation interrupted between the target compose up and <c>apply:after</c> (issue #3181).</summary>
public sealed partial class HostUpdateCliOfflineActivateTests
{
    private static readonly (HostUpdateExecutionState State, string Phase)[] InterruptedAtApply =
    [
        (HostUpdateExecutionState.Preflight, "preflight:before"),
        (HostUpdateExecutionState.Preflight, "preflight:after"),
        (HostUpdateExecutionState.Draining, "drain:before"),
        (HostUpdateExecutionState.Draining, "drain:after"),
        (HostUpdateExecutionState.Fenced, "fence:before"),
        (HostUpdateExecutionState.Fenced, "fence:after"),
        (HostUpdateExecutionState.BackedUp, "backup:before"),
        (HostUpdateExecutionState.BackedUp, "backup:after"),
        (HostUpdateExecutionState.Migrating, "migration:before"),
        (HostUpdateExecutionState.Migrating, "migration:after"),
        (HostUpdateExecutionState.Applying, "apply:before"),
    ];

    [HostStateFact]
    public async Task Activation_redrive_after_power_loss_at_apply_reconciles_running_target_writer()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        _host.SeedJournal(request, InterruptedAtApply, baseline: null, withBaseline: false);
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + TargetDigest(request, "monolith");

        JsonElement activated = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UseIntegratedBoundaries(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        activated.GetProperty("exitCode").GetInt32().Should().Be(HostUpdateCliExitCodes.Success, activated.ToString());
        runner.ComposeUpCalls.Should().BeEmpty("the interrupted apply is reconciled from the running digests, never repeated");
        runner.MigrationRunCalls.Should().BeEmpty("a completed migration is never replayed");
        runner.StoppedContainerIds.Should().BeEmpty("a writer already on the target is never stopped by the N-1 fence");
        (await new FileInstalledHostStateStore(InstalledStatePath()).ReadAsync(CancellationToken.None))!
            .ReleaseId.Should().Be(TargetReleaseId);
        IReadOnlyList<HostUpdateExecutionActivity> activities = new FileHostUpdateExecutionJournal(_host.JournalPath).Read(request.ReleaseId);
        activities.Should().ContainSingle(activity => activity.State == HostUpdateExecutionState.Applying && activity.Phase == "apply:after");
        activities[^1].Should().Match<HostUpdateExecutionActivity>(activity =>
            activity.State == HostUpdateExecutionState.Completed && activity.Phase == "completed");
    }

    [HostStateTheory]
    [InlineData("no-journal")]
    [InlineData("interrupted-at-migration")]
    [InlineData("apply-completed")]
    [InlineData("recovery-required")]
    [InlineData("other-request")]
    [InlineData("other-repository")]
    [InlineData("tag")]
    [InlineData("other-digest")]
    public async Task Activation_refuses_running_writer_unless_this_request_is_interrupted_at_apply_on_its_target_pin(string variant)
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        string targetPin = MonolithRepository + "@" + TargetDigest(request, "monolith");
        switch (variant)
        {
            case "no-journal":
                break;
            case "interrupted-at-migration":
                _host.SeedJournal(request, InterruptedAtApply[..9], baseline: null, withBaseline: false);
                break;
            case "apply-completed":
                _host.SeedJournal(request, [.. InterruptedAtApply, (HostUpdateExecutionState.Applying, "apply:after")], baseline: null, withBaseline: false);
                break;
            case "recovery-required":
                _host.SeedJournal(request, [.. InterruptedAtApply, (HostUpdateExecutionState.RecoveryRequired, "failure:uncertain_side_effect:apply")], baseline: null, withBaseline: false);
                break;
            case "other-request":
                _host.SeedJournal(request with { ImageSourceMode = HostUpdateImageSourceMode.Registry }, InterruptedAtApply, baseline: null, withBaseline: false);
                break;
            default:
                _host.SeedJournal(request, InterruptedAtApply, baseline: null, withBaseline: false);
                break;
        }

        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = variant switch
        {
            "other-repository" => "ghcr.io/attacker/printfarmer-monolith@" + TargetDigest(request, "monolith"),
            "tag" => MonolithRepository + ":latest",
            "other-digest" => MonolithRepository + "@sha256:" + new string('7', 64),
            _ => targetPin,
        };

        JsonElement refused = Envelope(await RunAsync(Activate(), IntegratedConfiguration(), services =>
            UseIntegratedBoundaries(services, runner, new RecordingHealthHttpClientFactory("http://localhost:5245", succeeds: true))));

        AssertRefused(refused, "writer_service_active:monolith:running");
        runner.ComposeUpCalls.Should().BeEmpty();
        runner.StoppedContainerIds.Should().BeEmpty();
    }

    [HostStateFact]
    public async Task Writer_absence_probe_never_records_an_in_flight_target_writer_for_the_prior_release_fence()
    {
        await ImportAsync();
        HostUpdateExecutionRequest request = RequestFromCurrentStaging();
        _host.SeedJournal(request, InterruptedAtApply, baseline: null, withBaseline: false);
        var runner = new IntegratedActivationProcessRunner(healthSucceeds: true);
        runner.RunningWriterImages["monolith"] = MonolithRepository + "@" + TargetDigest(request, "monolith");
        var context = new HostUpdatePriorReleaseContext();
        context.RecordTolerated(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["monolith"] = ["stale-1 stale"] });
        var options = new HostUpdateExecutionOptions { ActiveServiceIds = ["api", "frontend", "slicer-host", "monolith"] };
        var probe = new DockerComposeApiAbsenceProbe(
            runner,
            new DockerOnlyResolver(),
            options,
            context,
            installedStateStore: null,
            journal: new FileHostUpdateExecutionJournal(_host.JournalPath));

        (await probe.ValidateSafeToExecuteAsync(request, CancellationToken.None)).Should().BeNull();
        context.ToleratedWriters.Should().BeEmpty("only authenticated prior-release writers may be stopped by the N-1 fence");
        (await probe.ValidateWriterHostsStoppedAsync(CancellationToken.None))
            .Should().Be("writer_service_active:monolith:running", "the strict writer-host proof is unchanged");
    }

    private static string TargetDigest(HostUpdateExecutionRequest request, string serviceId) =>
        request.Targets.Single(target => target.ServiceId == serviceId).ChildDigest;
}
