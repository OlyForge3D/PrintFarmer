using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateImageApplierTests
{
    private static readonly HostUpdateApplyServiceMapping ApiMapping = new("api", "api", "PRINTFARMER_API_IMAGE", "ghcr.io/olyforge3d/printfarmer-api");

    [Fact]
    public async Task RunAsync_StagesSignedPlatformDigestBeforeComposeUpPullNever()
    {
        var runner = new RecordingProcessRunner(_ => new HostUpdateProcessResult(0, "ok", string.Empty));
        var applier = new HostUpdateImageApplier(
            runner,
            ["compose.yml"],
            "printfarmer",
            new Dictionary<string, HostUpdateApplyServiceMapping>(StringComparer.Ordinal) { ["api"] = ApiMapping },
            TimeSpan.FromSeconds(30));
        string digest = "sha256:" + new string('a', 64);
        var request = new HostUpdateExecutionRequest(
            "release-1",
            1,
            "sha256:" + new string('b', 64),
            new string('c', 40),
            HostUpdateExecutionChannel.Stable,
            [new HostUpdateExecutionTarget("api", "linux-amd64", digest)]);

        await applier.RunAsync(request, CancellationToken.None);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].FileName.Should().Be("docker");
        runner.Calls[0].Arguments.Should().Equal("image", "pull", "--platform", "linux-amd64", $"ghcr.io/olyforge3d/printfarmer-api@{digest}");
        runner.Calls[1].Arguments.Should().Equal("compose", "-f", "compose.yml", "-p", "printfarmer", "up", "-d", "--no-build", "--pull", "never", "api");
        runner.Calls[1].Environment.Should().ContainKey("PRINTFARMER_API_IMAGE").WhoseValue.Should().Be($"ghcr.io/olyforge3d/printfarmer-api@{digest}");
    }

    [Fact]
    public async Task RunAsync_StagingFailureNeverRunsComposeMutation()
    {
        var runner = new RecordingProcessRunner(_ => new HostUpdateProcessResult(1, string.Empty, "unauthorized"));
        var applier = new HostUpdateImageApplier(
            runner,
            ["compose.yml"],
            "printfarmer",
            new Dictionary<string, HostUpdateApplyServiceMapping>(StringComparer.Ordinal) { ["api"] = ApiMapping },
            TimeSpan.FromSeconds(30));
        string digest = "sha256:" + new string('a', 64);
        var request = new HostUpdateExecutionRequest(
            "release-1",
            1,
            "sha256:" + new string('b', 64),
            new string('c', 40),
            HostUpdateExecutionChannel.Stable,
            [new HostUpdateExecutionTarget("api", "linux-amd64", digest)]);

        Func<Task> act = () => applier.RunAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<HostUpdateImageStagingFailedException>()
            .Where(ex => ex.ServiceId == "api" && ex.ExitCode == 1 && ex.StandardError == "unauthorized");
        runner.Calls.Should().ContainSingle();
        runner.Calls[0].Arguments.Should().StartWith(["image", "pull"]);
    }

    [Fact]
    public async Task ApplyByDigestsAsync_RecoveryStagesImmutableDigestBeforeComposeWithoutPlatformClaim()
    {
        var runner = new RecordingProcessRunner(_ => new HostUpdateProcessResult(0, "ok", string.Empty));
        var applier = new HostUpdateImageApplier(
            runner,
            ["compose.yml"],
            "printfarmer",
            new Dictionary<string, HostUpdateApplyServiceMapping>(StringComparer.Ordinal) { ["api"] = ApiMapping },
            TimeSpan.FromSeconds(30));
        string digest = "sha256:" + new string('a', 64);

        await applier.ApplyByDigestsAsync(new Dictionary<string, string>(StringComparer.Ordinal) { ["api"] = digest }, CancellationToken.None);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Arguments.Should().Equal("image", "pull", $"ghcr.io/olyforge3d/printfarmer-api@{digest}");
        runner.Calls[1].Arguments.Should().ContainInOrder("--pull", "never");
    }


    [Fact]
    public async Task ApplyByDigestsAsync_RecoveryUsesPersistedPlatformEvidenceWhenAvailable()
    {
        var runner = new RecordingProcessRunner(_ => new HostUpdateProcessResult(0, "ok", string.Empty));
        var applier = new HostUpdateImageApplier(
            runner,
            ["compose.yml"],
            "printfarmer",
            new Dictionary<string, HostUpdateApplyServiceMapping>(StringComparer.Ordinal) { ["api"] = ApiMapping },
            TimeSpan.FromSeconds(30));
        string digest = "sha256:" + new string('a', 64);

        await applier.ApplyByDigestsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["api"] = digest },
            CancellationToken.None,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["api"] = "linux-arm64" });

        runner.Calls[0].Arguments.Should().Equal("image", "pull", "--platform", "linux-arm64", $"ghcr.io/olyforge3d/printfarmer-api@{digest}");
        runner.Calls[1].Arguments.Should().ContainInOrder("--pull", "never");
    }
    private sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

    private sealed class RecordingProcessRunner(Func<ProcessCall, HostUpdateProcessResult> onRun) : IHostUpdateProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<HostUpdateProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var call = new ProcessCall(fileName, arguments.ToArray(), environment is null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment, StringComparer.Ordinal));
            Calls.Add(call);
            return Task.FromResult(onRun(call));
        }
    }
}
