using System.Globalization;
using System.Text.Json.Nodes;
using Farm.OrcaSlicer.Worker.Services;
using Farm.OrcaSlicer.Worker.Tests.Support;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Tests.Calibration;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.IntegrationTests.Calibration;

/// <summary>
/// Executes the real, pinned OrcaSlicer CLI (issue #1802) against a multi-axis, negative-Z
/// viewer rotation and compares the resulting G-code's object orientation against a matrix
/// derived independently of production code.
/// </summary>
/// <remarks>
/// <para>
/// Issue #1794 fixed a defect where a viewer rotation of X=22.92°, Y=51.57°, Z=-74.48° was
/// mis-oriented by 129° by OrcaSlicer's CLI. <see cref="BuildTransformFlagsTests"/>'s
/// <c>SimulateOrcaCli</c>/<c>ExtractEulerAnglesLikeOrca</c> hand-transcribe OrcaSlicer's own
/// Euler-angle extraction and composition math so unit tests can assert against it without a
/// container — but a hand transcription can silently drift from the real binary it models. This
/// class is that transcription's external oracle: it calls the exact same production functions
/// (<see cref="OrcaSlicingPipelineService.BuildTransformFlags"/>,
/// <see cref="OrcaSlicingPipelineService.PlanPlacement"/>,
/// <see cref="OrcaSlicingPipelineService.BuildOrcaSlicerArguments"/>,
/// <see cref="ThreeMfProjectBuilder.Build"/>) that a real production job would, runs the actual
/// pinned OrcaSlicer 2.4.2 binary inside <see cref="PinnedOrcaWorkerContainer"/> against those
/// arguments, and measures the sliced G-code's own motion commands
/// (<see cref="OrcaGcodeOrientationReader"/>) — never production code — against an expected
/// bounding-box size computed purely from the viewer's <c>Rx·Ry·Rz</c> matrix
/// (<see cref="OrientationMarkerGeometry.ComputeExpectedSize"/>).
/// </para>
/// <para>
/// Removing the negative-Z correction in <c>ToOrcaRotation</c>, or regressing
/// <c>BuildTransformFlags</c> back to emitting the workspace's raw Euler angles verbatim, both
/// change the real CLI flags this test sends to the real binary — and because the marker solid
/// is deliberately asymmetric (<see cref="OrientationMarkerGeometry"/>), either regression
/// produces a real, measurable bounding-box divergence from the independently-computed expected
/// size, not a divergence hidden by code shared between "expected" and "actual".
/// </para>
/// <para>
/// The worker's raw upstream profiles (<see cref="PinnedOrcaProfileCatalog"/>) legitimately carry
/// vendor command fields — <c>machine_start_gcode</c>, <c>before_layer_change_gcode</c>,
/// <c>change_filament_gcode</c> and similar hooks routinely emit purge lines, wipe towers and
/// other extruding moves at fixed physical positions unrelated to the marker's own orientation.
/// Left untouched, those moves would leak into <see cref="OrcaGcodeOrientationReader"/>'s measured
/// bounding box and make this test's pass/fail depend on vendor profile scripting instead of the
/// model's rotation. Before slicing, this class derives each profile through
/// <see cref="OrcaEffectiveProfileFactory.Derive(string)"/> — the same
/// <see cref="OrcaProfileCommandKeys"/> neutralization rule a real calibration job applies before
/// it ever reaches a worker — so the real binary here only ever sees profiles with every
/// <c>*_gcode</c> hook (plus <c>post_process</c>/<c>printer_notes</c>) emptied out, and the
/// measured extents can only reflect the marker itself.
/// </para>
/// <para>
/// Gated by the shared <see cref="PinnedOrcaPublication.SmokeCategory"/> trait and the same
/// <see cref="PinnedOrcaPublication.ResolveGate"/> operational gate every pinned-worker smoke test
/// shares, so it only executes the real binary where a published, digest-pinned worker image and
/// Docker are both available (manually dispatched CI via <c>orcaslicer-strict-build.yml</c> with
/// <c>publish_pinned_worker=true</c>, or a local run with the gate's environment variable set) —
/// it never runs the real CLI on every ordinary PR build.
/// </para>
/// </remarks>
[Trait("Category", PinnedOrcaPublication.SmokeCategory)]
public sealed class PinnedOrcaCliRotationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan WorkerStartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Tolerance for the bounding-box size comparison, millimetres.</summary>
    private const double ToleranceMillimeters = 2.0;

    /// <summary>
    /// The viewer Euler angles (degrees, three.js 'XYZ') that issue #1794 fixed: before that fix
    /// OrcaSlicer's CLI mis-oriented this exact rotation by 129°.
    /// </summary>
    private const double RotationXDegrees = 22.92;

    private const double RotationYDegrees = 51.57;
    private const double RotationZDegrees = -74.48;

    private readonly ITestOutputHelper _output = output ?? throw new ArgumentNullException(nameof(output));
    private string _workspace = string.Empty;
    private KestrelCalibrationApiHost _api = null!;

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"pfarm-orca-cli-rotation-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(_workspace);
        _api = KestrelCalibrationApiHost.Start(_workspace);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (IOException)
        {
            // A worker still holding a handle must not turn cleanup into a test failure.
        }
    }

    [Fact(DisplayName =
        "The real OrcaSlicer CLI rotates a multi-axis negative-Z model to the viewer's Rx*Ry*Rz " +
        "orientation via CLI rotate flags (auto-arrange placement)")]
    public Task RealOrcaSlicerCli_MultiAxisNegativeZRotation_MatchesViewerOrientation_CliFlagsPlacement() =>
        RunAsync(PlacementCase.CliFlags);

    [Fact(DisplayName =
        "The real OrcaSlicer CLI rotates a multi-axis negative-Z model to the viewer's Rx*Ry*Rz " +
        "orientation via a baked 3MF project transform (positioned placement)")]
    public Task RealOrcaSlicerCli_MultiAxisNegativeZRotation_MatchesViewerOrientation_ThreeMfProjectPlacement() =>
        RunAsync(PlacementCase.ThreeMfProject);

    private enum PlacementCase
    {
        /// <summary>Rotation only, no custom position: routed through <c>--rotate*</c> CLI flags.</summary>
        CliFlags,

        /// <summary>Rotation plus a custom position: routed through a baked 3MF project transform.</summary>
        ThreeMfProject,
    }

    private async Task RunAsync(PlacementCase placementCase)
    {
        using CancellationTokenSource timeout = new(OverallTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await _api.WaitUntilHealthyAsync(TimeSpan.FromMinutes(2), cancellationToken);

        PinnedOrcaSmokeGate gate = PinnedOrcaPublication.ResolveGate(Environment.GetEnvironmentVariable);
        gate = await ConfirmDockerAsync(gate, cancellationToken);
        _output.WriteLine(gate.Describe());
        if (!gate.CanRun)
        {
            // Same honest-blocker contract every pinned-worker smoke test shares: a required-but
            // -blocked gate fails the run; an optional one reports the blocker and skips.
            _ = gate.IsRequired.Should().BeFalse(
                "the operational gate was required but could not execute: " + gate.BlockReason);
            return;
        }

        PinnedOrcaWorkerContainer.CommandResult pull =
            await PinnedOrcaWorkerContainer.PullAsync(gate.ImageReference, cancellationToken);
        _ = pull.ExitCode.Should().Be(0, "the published pinned worker must be pullable by digest: " + pull.Describe());

        await using PinnedOrcaWorkerContainer worker = await PinnedOrcaWorkerContainer.StartAsync(
            gate.ImageReference,
            gate.Digest!,
            _api.BaseAddress,
            _api.WorkerSharedKey,
            cancellationToken);
        await worker.WaitUntilReachableAsync(WorkerStartTimeout, cancellationToken);

        PinnedOrcaProfileSelection profiles =
            await PinnedOrcaProfileCatalog.SelectAsync(worker.BaseAddress, cancellationToken);

        // PinnedOrcaProfileCatalog deliberately hands back the worker's raw upstream documents
        // unchanged (including vendor command fields such as machine_start_gcode,
        // before_layer_change_gcode and change_filament_gcode). Those hooks routinely contain
        // purge lines, wipe towers and other extruding moves at fixed physical positions that
        // have nothing to do with the marker's own rotation — left in place, they would
        // contaminate OrcaGcodeOrientationReader's measured bounding box and make this test's
        // pass/fail depend on vendor profile scripting instead of the model orientation. This
        // reuses the exact same neutralization rule production's own calibration plan compiler
        // applies before a real job ever reaches a worker (OrcaEffectiveProfileFactory /
        // OrcaProfileCommandKeys: any key ending in "_gcode", plus post_process/printer_notes),
        // so the sliced output here reflects only the marker being rotated.
        string machineJson = OrcaEffectiveProfileFactory.Derive(profiles.MachineJson).Json;
        string processJson = DisableSkirtAndBrim(OrcaEffectiveProfileFactory.Derive(profiles.ProcessJson).Json);
        string filamentJson = OrcaEffectiveProfileFactory.Derive(profiles.FilamentJson).Json;
        (double X, double Y)? bedCenter = OrcaSlicingPipelineService.TryReadBedCenter(machineJson);
        _ = bedCenter.Should().NotBeNull("the pinned worker's own machine profile must declare a printable_area");

        string localWorkDir = Path.Combine(_workspace, placementCase.ToString());
        _ = Directory.CreateDirectory(localWorkDir);
        string localMarkerStl = Path.Combine(localWorkDir, "marker.stl");
        OrientationMarkerGeometry.WriteBinaryStl(localMarkerStl);

        string localMachineJson = Path.Combine(localWorkDir, "machine.json");
        string localProcessJson = Path.Combine(localWorkDir, "process.json");
        string localFilamentJson = Path.Combine(localWorkDir, "filament.json");
        await File.WriteAllTextAsync(localMachineJson, machineJson, cancellationToken);
        await File.WriteAllTextAsync(localProcessJson, processJson, cancellationToken);
        await File.WriteAllTextAsync(localFilamentJson, filamentJson, cancellationToken);

        string containerWorkDir = FormattableString.Invariant(
            $"/work/pinned-orca-cli-rotation-{Guid.NewGuid():N}");
        string containerMachineJson = $"{containerWorkDir}/machine.json";
        string containerProcessJson = $"{containerWorkDir}/process.json";
        string containerFilamentJson = $"{containerWorkDir}/filament.json";
        string containerOutputDir = $"{containerWorkDir}/output";

        _ = await worker.ExecAsync(["mkdir", "-p", containerOutputDir], cancellationToken);
        await CopyOrThrowAsync(worker, localMachineJson, containerMachineJson, cancellationToken);
        await CopyOrThrowAsync(worker, localProcessJson, containerProcessJson, cancellationToken);
        await CopyOrThrowAsync(worker, localFilamentJson, containerFilamentJson, cancellationToken);

        double rx = RotationXDegrees * Math.PI / 180.0;
        double ry = RotationYDegrees * Math.PI / 180.0;
        double rz = RotationZDegrees * Math.PI / 180.0;

        (string arrangeFlag, string transformFlags, string containerModelPath) = placementCase == PlacementCase.CliFlags
            ? await PrepareCliFlagsPlacementAsync(worker, localMarkerStl, containerWorkDir, rx, ry, rz, cancellationToken)
            : await PrepareThreeMfProjectPlacementAsync(
                worker, localMarkerStl, localWorkDir, containerWorkDir, bedCenter!.Value, rx, ry, rz, cancellationToken);

        string arguments = OrcaSlicingPipelineService.BuildOrcaSlicerArguments(
            arrangeFlag,
            transformFlags,
            pipeFlag: string.Empty,
            plateFlag: string.Empty,
            containerMachineJson,
            containerProcessJson,
            containerFilamentJson,
            containerOutputDir,
            [containerModelPath]);

        const string OrcaBinaryPath = "/opt/orcaslicer/bin/orca-slicer";
        PinnedOrcaWorkerContainer.CommandResult xvfbCheck =
            await worker.ExecAsync(["test", "-x", "/usr/bin/xvfb-run"], cancellationToken);

        // BuildOrcaSlicerArguments composes one shell-style argument string designed for
        // ProcessStartInfo.Arguments (production runs the binary as a local process, not inside
        // a container). docker exec has no shell of its own — it invokes ArgumentList entries
        // directly with no further parsing — so "sh -c" is used here to reuse that exact string
        // unmodified, exactly as production's own quoting was designed to be interpreted.
        string command = xvfbCheck.ExitCode == 0
            ? $"/usr/bin/xvfb-run -a {OrcaBinaryPath} {arguments}"
            : $"{OrcaBinaryPath} {arguments}";
        _output.WriteLine("OrcaSlicer command: " + command);

        PinnedOrcaWorkerContainer.CommandResult sliceResult =
            await worker.ExecAsync(["sh", "-c", command], cancellationToken);
        _ = sliceResult.ExitCode.Should().Be(
            0,
            "the real OrcaSlicer CLI must slice the rotated marker: " + sliceResult.Describe());

        string localOutputDir = Path.Combine(localWorkDir, "output");
        _ = Directory.CreateDirectory(localOutputDir);
        PinnedOrcaWorkerContainer.CommandResult copyResult =
            await worker.CopyFromContainerAsync(containerOutputDir, localWorkDir, cancellationToken);
        _ = copyResult.ExitCode.Should().Be(0, "the sliced output must be retrievable: " + copyResult.Describe());

        // Production itself does not assume a fixed output filename (RunOrcaSlicerAsync falls
        // back through several candidates before globbing), so this glob matches that final
        // fallback rather than assuming a specific name.
        string[] gcodeFiles = Directory.GetFiles(localWorkDir, "*.gcode", SearchOption.AllDirectories);
        _ = gcodeFiles.Should().NotBeEmpty("the real OrcaSlicer CLI must have produced at least one .gcode file");

        string[] lines = await File.ReadAllLinesAsync(gcodeFiles[0], cancellationToken);
        GcodeExtent actual = OrcaGcodeOrientationReader.ComputeExtrusionExtent(lines);
        (double expectedX, double expectedY, double expectedZ) = OrientationMarkerGeometry.ComputeExpectedSize(rx, ry, rz);

        _output.WriteLine(
            $"Expected size (viewer Rx*Ry*Rz): X={expectedX:F3} Y={expectedY:F3} Z={expectedZ:F3}");
        _output.WriteLine(
            $"Actual size (real OrcaSlicer CLI G-code): X={actual.SizeX:F3} Y={actual.SizeY:F3} Z={actual.SizeZ:F3}");

        _ = actual.SizeX.Should().BeApproximately(
            expectedX, ToleranceMillimeters, "the real CLI's X extent must match the viewer's Rx*Ry*Rz orientation");
        _ = actual.SizeY.Should().BeApproximately(
            expectedY, ToleranceMillimeters, "the real CLI's Y extent must match the viewer's Rx*Ry*Rz orientation");
        _ = actual.SizeZ.Should().BeApproximately(
            expectedZ, ToleranceMillimeters, "the real CLI's Z extent must match the viewer's Rx*Ry*Rz orientation");
    }

    /// <summary>
    /// Rotation-only transform (no custom position): production routes this through
    /// <see cref="OrcaSlicingPipelineService.PlacementStrategy.AutoArrange"/> and the model's
    /// rotation is expressed as <c>--rotate*</c> CLI flags built by
    /// <see cref="OrcaSlicingPipelineService.BuildTransformFlags"/>.
    /// </summary>
    private static async Task<(string ArrangeFlag, string TransformFlags, string ContainerModelPath)> PrepareCliFlagsPlacementAsync(
        PinnedOrcaWorkerContainer worker,
        string localMarkerStl,
        string containerWorkDir,
        double rx,
        double ry,
        double rz,
        CancellationToken cancellationToken)
    {
        string containerModelPath = $"{containerWorkDir}/marker.stl";
        await CopyOrThrowAsync(worker, localMarkerStl, containerModelPath, cancellationToken);

        string transformJson =
            $$"""{"rotation":[{{Inv(rx)}},{{Inv(ry)}},{{Inv(rz)}}],"scale":[1,1,1]}""";

        OrcaSlicingPipelineService.PlacementPlan plan = OrcaSlicingPipelineService.PlanPlacement(
            transformJson,
            modelFileTransforms: null,
            [containerModelPath],
            bedCenterKnown: true);
        _ = plan.Strategy.Should().Be(
            OrcaSlicingPipelineService.PlacementStrategy.AutoArrange,
            "no custom position was requested, so PlanPlacement must not route this through a 3MF project");
        _ = plan.TransformFlags.Should().Contain(
            "--rotate", "the rotation must be expressed as OrcaSlicer CLI rotate flags for this placement");

        return (plan.ArrangeFlag, plan.TransformFlags, containerModelPath);
    }

    /// <summary>
    /// Rotation plus a non-zero position: production routes this through
    /// <see cref="OrcaSlicingPipelineService.PlacementStrategy.ThreeMfProject"/>, baking rotation
    /// and position into the 3MF build-item transform via
    /// <see cref="ThreeMfProjectBuilder.Build"/> instead of emitting CLI rotate flags.
    /// </summary>
    private static async Task<(string ArrangeFlag, string TransformFlags, string ContainerModelPath)> PrepareThreeMfProjectPlacementAsync(
        PinnedOrcaWorkerContainer worker,
        string localMarkerStl,
        string localWorkDir,
        string containerWorkDir,
        (double X, double Y) bedCenter,
        double rx,
        double ry,
        double rz,
        CancellationToken cancellationToken)
    {
        string transformJson =
            $$"""{"rotation":[{{Inv(rx)}},{{Inv(ry)}},{{Inv(rz)}}],"scale":[1,1,1],"position":[10,-5,0]}""";

        OrcaSlicingPipelineService.PlacementPlan plan = OrcaSlicingPipelineService.PlanPlacement(
            transformJson,
            modelFileTransforms: null,
            [localMarkerStl],
            bedCenterKnown: true);
        _ = plan.Strategy.Should().Be(
            OrcaSlicingPipelineService.PlacementStrategy.ThreeMfProject,
            "a custom position on an STL input with a known bed centre must be embedded in a 3MF project");
        _ = plan.TransformFlags.Should().BeEmpty(
            "a 3MF project placement bakes rotation into the build-item transform, not CLI flags");

        string local3mfPath = ThreeMfProjectBuilder.Build(
            [new ThreeMfProjectBuilder.ModelEntry(localMarkerStl, transformJson)],
            localWorkDir,
            bedCenter);

        string containerModelPath = $"{containerWorkDir}/project.3mf";
        await CopyOrThrowAsync(worker, local3mfPath, containerModelPath, cancellationToken);

        return (plan.ArrangeFlag, plan.TransformFlags, containerModelPath);
    }

    private static async Task CopyOrThrowAsync(
        PinnedOrcaWorkerContainer worker,
        string hostPath,
        string containerPath,
        CancellationToken cancellationToken)
    {
        PinnedOrcaWorkerContainer.CommandResult result =
            await worker.CopyToContainerAsync(hostPath, containerPath, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not copy '{hostPath}' into the container at '{containerPath}': {result.Describe()}");
        }
    }

    /// <summary>
    /// Zeroes out the skirt loop count and brim width on a process profile so the sliced
    /// G-code's extruding moves reflect only the marker itself, not an auxiliary perimeter that
    /// would otherwise inflate the measured XY bounding box.
    /// </summary>
    private static string DisableSkirtAndBrim(string processJson)
    {
        if (JsonNode.Parse(processJson) is not JsonObject settings)
        {
            return processJson;
        }

        if (settings.ContainsKey("skirt_loops"))
        {
            settings["skirt_loops"] = "0";
        }

        if (settings.ContainsKey("skirt_height"))
        {
            settings["skirt_height"] = "0";
        }

        if (settings.ContainsKey("brim_width"))
        {
            settings["brim_width"] = "0";
        }

        if (settings.ContainsKey("brim_type"))
        {
            settings["brim_type"] = "no_brim";
        }

        return settings.ToJsonString();
    }

    private static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static async Task<PinnedOrcaSmokeGate> ConfirmDockerAsync(
        PinnedOrcaSmokeGate gate,
        CancellationToken cancellationToken) =>
        !gate.CanRun || await PinnedOrcaWorkerContainer.HasDockerAsync(cancellationToken)
            ? gate
            : gate with
            {
                Image = null,
                Digest = null,
                BlockReason = "no usable docker command was found, so the published pinned worker cannot be executed.",
            };
}
