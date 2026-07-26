using System.Diagnostics;
using System.Security.Cryptography;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Mandatory smoke test for the real pinned OrcaSlicer build identity.
/// </summary>
/// <remarks>
/// <para>
/// The test only executes the slicer when a real pinned binary or container image is present together
/// with its authoritative checksum or digest. It never substitutes a stub, a script or a recorded
/// output: a success here always means the pinned upstream build actually ran on this machine.
/// </para>
/// <para>
/// When the pinned build is not available the test reports the concrete blocker and asserts that the
/// calibration generation capability stays false, which is the only honest outcome for a deployment
/// that cannot prove its slicer identity.
/// </para>
/// </remarks>
public sealed class CalibrationPinnedOrcaSmokeTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>Absolute path of the pinned OrcaSlicer binary or AppImage.</summary>
    public const string BinaryPathVariable = "PRINTFARMER_ORCASLICER_BINARY";

    /// <summary>Authoritative SHA-256 of the pinned OrcaSlicer binary or AppImage.</summary>
    public const string BinaryDigestVariable = "PRINTFARMER_ORCASLICER_SHA256";

    /// <summary>Pinned OrcaSlicer worker container image reference.</summary>
    public const string ImageVariable = "PRINTFARMER_ORCASLICER_IMAGE";

    /// <summary>Authoritative digest of the pinned OrcaSlicer worker container image.</summary>
    public const string ImageDigestVariable = "PRINTFARMER_ORCASLICER_IMAGE_DIGEST";

    /// <summary>Authoritative upstream checksum used by the strict container build.</summary>
    public const string CiDigestVariable = "ORCASLICER_SHA256";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);

    private readonly ITestOutputHelper _output = output ?? throw new ArgumentNullException(nameof(output));

    private CalibrationGenerationHarness _harness = null!;

    public async Task InitializeAsync() => _harness = await CalibrationGenerationHarness.CreateAsync();

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "The real pinned OrcaSlicer build runs, or the deployment stays non-operational")]
    public async Task PinnedOrcaSlicer_RunsOrKeepsGenerationUnavailable()
    {
        PinnedOrcaAvailability availability = PinnedOrcaAvailability.Discover();
        _output.WriteLine(availability.Describe());

        if (!availability.IsAvailable)
        {
            // No fake stands in for the pinned build. The only thing asserted is that a deployment
            // which cannot prove its slicer identity never advertises generation as operational.
            CalibrationGenerationCapabilityDto capability = await _harness
                .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
                .GetCapabilityAsync(CancellationToken.None);

            _ = availability.BlockReason.Should().NotBeNullOrWhiteSpace(
                "an unavailable pinned build must always name its concrete blocker");
            _ = capability.Operational.Should().BeFalse();
            _ = capability.PinnedWorkerAvailable.Should().BeFalse();
            _ = capability.UnavailableCode.Should()
                .Be(CalibrationGenerationProblemCodes.PinnedWorkerUnavailable);
            return;
        }

        PinnedOrcaExecution execution = await availability.ExecuteAsync(CancellationToken.None);
        _output.WriteLine(execution.Describe());

        _ = execution.ExitCode.Should().Be(0, execution.Diagnostics);
        _ = execution.ReportedVersion.Should().Contain(
            CalibrationContractConstants.SlicerVersion,
            "the executed build must be the pinned upstream version");
        _ = execution.VerifiedDigest.Should().BeTrue(
            "the executed artifact must match its authoritative checksum or digest");

        // A proven pinned build makes the worker attestation hop satisfiable end to end.
        _ = await _harness.AddAttestedWorkerAsync(
            containerDigest: availability.ImageDigest ?? CalibrationGenerationSeed.ContainerDigest,
            binaryDigest: availability.BinaryDigest ?? CalibrationGenerationSeed.BinaryDigest);
        CalibrationGenerationCapabilityDto operational = await _harness
            .CreateCapabilityProbe(new CalibrationGenerationHarnessOptions())
            .GetCapabilityAsync(CancellationToken.None);
        _ = operational.PinnedWorkerAvailable.Should().BeTrue();
        _ = operational.Operational.Should().BeTrue(operational.UnavailableCode);
    }

    /// <summary>How the pinned OrcaSlicer build can be exercised on this machine, if at all.</summary>
    /// <param name="BinaryPath">Absolute path of the pinned binary, when configured.</param>
    /// <param name="BinaryDigest">Authoritative binary checksum, when configured.</param>
    /// <param name="Image">Pinned container image reference, when configured.</param>
    /// <param name="ImageDigest">Authoritative container digest, when configured.</param>
    /// <param name="BlockReason">The concrete reason the pinned build cannot be exercised.</param>
    private sealed record PinnedOrcaAvailability(
        string? BinaryPath,
        string? BinaryDigest,
        string? Image,
        string? ImageDigest,
        string? BlockReason)
    {
        public bool IsAvailable => BlockReason is null;

        public static PinnedOrcaAvailability Discover()
        {
            string? binaryPath = Read(BinaryPathVariable);
            string? binaryDigest = Read(BinaryDigestVariable) ?? Read(CiDigestVariable);
            string? image = Read(ImageVariable);
            string? imageDigest = Read(ImageDigestVariable);

            if (binaryPath is not null)
            {
                if (!File.Exists(binaryPath))
                {
                    return Blocked(
                        binaryPath,
                        binaryDigest,
                        image,
                        imageDigest,
                        $"{BinaryPathVariable} points at a file that does not exist on this machine.");
                }

                return binaryDigest is null
                    ? Blocked(
                        binaryPath,
                        binaryDigest,
                        image,
                        imageDigest,
                        $"{BinaryDigestVariable} (or {CiDigestVariable}) is not set, so the binary identity cannot be verified.")
                    : new PinnedOrcaAvailability(binaryPath, binaryDigest, image, imageDigest, null);
            }

            if (image is not null)
            {
                if (imageDigest is null)
                {
                    return Blocked(
                        binaryPath,
                        binaryDigest,
                        image,
                        imageDigest,
                        $"{ImageDigestVariable} is not set, so the container identity cannot be verified.");
                }

                return !HasDockerCli()
                    ? Blocked(
                        binaryPath,
                        binaryDigest,
                        image,
                        imageDigest,
                        "no usable docker command was found, so the pinned image cannot be executed.")
                    : new PinnedOrcaAvailability(binaryPath, binaryDigest, image, imageDigest, null);
            }

            return Blocked(
                binaryPath,
                binaryDigest,
                image,
                imageDigest,
                $"neither {BinaryPathVariable} nor {ImageVariable} is set, so no real pinned OrcaSlicer build is available to this test run.");
        }

        public string Describe() => IsAvailable
            ? $"Pinned OrcaSlicer smoke test will execute: binary='{BinaryPath ?? "(none)"}', image='{Image ?? "(none)"}'."
            : $"Pinned OrcaSlicer smoke test did not execute. Blocker: {BlockReason}";

        public async Task<PinnedOrcaExecution> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (BinaryPath is not null)
            {
                string actual = Convert.ToHexString(
                        await SHA256.HashDataAsync(
                            File.OpenRead(BinaryPath),
                            cancellationToken))
                    .ToLowerInvariant();
                (int exitCode, string stdout, string stderr) =
                    await RunAsync(BinaryPath, ["--version"], cancellationToken);
                return new PinnedOrcaExecution(
                    exitCode,
                    string.IsNullOrWhiteSpace(stdout) ? stderr : stdout,
                    string.Equals(actual, BinaryDigest!.Trim().ToLowerInvariant(), StringComparison.Ordinal),
                    $"binary='{BinaryPath}' expectedDigest='{BinaryDigest}' actualDigest='{actual}' stderr='{stderr}'");
            }

            (int inspectExit, string inspectOut, string inspectError) = await RunAsync(
                "docker",
                ["image", "inspect", "--format", "{{index .RepoDigests 0}}", Image!],
                cancellationToken);
            (int runExit, string runOut, string runError) = await RunAsync(
                "docker",
                ["run", "--rm", "--entrypoint", "orcaslicer", Image!, "--version"],
                cancellationToken);
            return new PinnedOrcaExecution(
                runExit,
                string.IsNullOrWhiteSpace(runOut) ? runError : runOut,
                inspectExit == 0 &&
                    inspectOut.Contains(ImageDigest!.Trim(), StringComparison.OrdinalIgnoreCase),
                $"image='{Image}' expectedDigest='{ImageDigest}' inspected='{inspectOut.Trim()}' inspectError='{inspectError}' runError='{runError}'");
        }

        private static PinnedOrcaAvailability Blocked(
            string? binaryPath,
            string? binaryDigest,
            string? image,
            string? imageDigest,
            string reason) =>
            new(binaryPath, binaryDigest, image, imageDigest, reason);

        private static string? Read(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool HasDockerCli()
        {
            try
            {
                (int exitCode, string _, string _) =
                    RunAsync("docker", ["--version"], CancellationToken.None).GetAwaiter().GetResult();
                return exitCode == 0;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
            {
                return false;
            }
        }

        private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new IOException($"The process '{fileName}' could not be started.");
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProcessTimeout);

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await standardOutput, await standardError);
        }
    }

    /// <summary>The observed outcome of executing the pinned build.</summary>
    /// <param name="ExitCode">Process exit code.</param>
    /// <param name="ReportedVersion">Version text the pinned build reported.</param>
    /// <param name="VerifiedDigest">Whether the authoritative checksum or digest matched.</param>
    /// <param name="Diagnostics">Operator-facing diagnostics for a failed run.</param>
    private sealed record PinnedOrcaExecution(
        int ExitCode,
        string ReportedVersion,
        bool VerifiedDigest,
        string Diagnostics)
    {
        public string Describe() =>
            $"Pinned OrcaSlicer executed: exitCode={ExitCode}, verifiedDigest={VerifiedDigest}, version='{ReportedVersion.Trim()}'.";
    }
}
