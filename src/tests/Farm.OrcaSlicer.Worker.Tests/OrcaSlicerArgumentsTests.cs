using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;
using static Farm.OrcaSlicer.Worker.Services.OrcaSlicingPipelineService;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Regression tests for the OrcaSlicer command line (#1794).
/// <para>
/// OrcaSlicer 2.4.2 compiles <c>center</c> and <c>align_xy</c> out of its
/// <c>CLITransformConfigDef</c>, so <c>--center</c> is rejected with
/// <c>Invalid option --center</c> and exit 254 (<c>CLI_INVALID_PARAMS</c>) before anything is
/// sliced. These tests pin the argument string and guard against the flag coming back.
/// </para>
/// </summary>
public class OrcaSlicerArgumentsTests
{
    private const string MachineJson = "/work/machine.json";
    private const string ProcessJson = "/work/process.json";
    private const string FilamentJson = "/work/filament.json";
    private const string OutputDir = "/work/output";
    private const string ModelStl = "/work/DumpTruck.stl";
    private const string ProjectThreeMf = "/work/project.3mf";

    /// <summary>The exact layout observed in the failing job (issue #1794): the second model
    /// added to a plate gets position [30, 0, 0] from the workspace.</summary>
    private const string PositionedTransform = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[30,0,0]}""";

    private const string OriginTransform = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";

    private static string Compose(PlacementPlan plan, params string[] modelPaths) =>
        BuildOrcaSlicerArguments(
            plan.ArrangeFlag,
            plan.TransformFlags,
            string.Empty,
            string.Empty,
            MachineJson,
            ProcessJson,
            FilamentJson,
            OutputDir,
            modelPaths);

    /// <summary>
    /// Mirrors the production call site: when the plan embeds placement in a 3MF project, the
    /// generated project replaces the source models on the command line. Keeping this coupling
    /// in the test means a pinned argument string reacts to a change of strategy, not just to a
    /// change of flags.
    /// </summary>
    private static string ComposeForPlan(PlacementPlan plan, string threeMfPath, params string[] modelPaths) =>
        Compose(plan, plan.Strategy == PlacementStrategy.ThreeMfProject ? [threeMfPath] : modelPaths);

    #region Positioned single model — the #1794 scenario

    [Fact]
    public void PositionedSingleModel_UsesThreeMfProject()
    {
        PlacementPlan plan = PlanPlacement(
            PositionedTransform,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.ThreeMfProject);
        plan.ArrangeFlag.Should().Be("--arrange 0");
        plan.TransformFlags.Should().BeEmpty("rotation and scale are baked into the 3MF matrix");
        plan.PositionDropped.Should().BeFalse();
        plan.ModelTransforms.Should().ContainSingle().Which.Should().Be(PositionedTransform);
    }

    /// <summary>
    /// Pins the whole command line for a positioned single model. The 3MF project replaces the
    /// STL as the input, and no <c>--center</c> appears anywhere.
    /// </summary>
    [Fact]
    public void PositionedSingleModel_ArgumentStringIsPinned()
    {
        PlacementPlan plan = PlanPlacement(
            PositionedTransform,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        string args = ComposeForPlan(plan, ProjectThreeMf, ModelStl);

        args.Should().Be(
            "--slice 0 --arrange 0 --ensure-on-bed " +
            "--load-settings \"/work/machine.json;/work/process.json\" " +
            "--load-filaments \"/work/filament.json\" " +
            "--allow-newer-file --outputdir \"/work/output\" \"/work/project.3mf\"");
    }

    [Fact]
    public void PositionedSingleModel_ArgumentStringNeverContainsCenter()
    {
        PlacementPlan plan = PlanPlacement(
            PositionedTransform,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        // Deliberately composed with the flags BuildTransformFlags actually produces for this
        // transform, NOT with plan.TransformFlags: the 3MF branch hard-codes the latter to
        // empty, so asserting on it would pass even with --center fully reintroduced.
        string args = BuildOrcaSlicerArguments(
            plan.ArrangeFlag,
            BuildTransformFlags(PositionedTransform).Flags,
            string.Empty,
            string.Empty,
            MachineJson,
            ProcessJson,
            FilamentJson,
            OutputDir,
            [ProjectThreeMf]);

        args.Should().NotContain("--center");
        args.Should().NotContain("--align-xy");
    }

    /// <summary>
    /// A positioned model must never take <c>--arrange 0</c> without an embedded placement:
    /// that combination leaves the model at raw mesh coordinates, which is exactly the
    /// mis-placement the <c>--center</c> flag was (incorrectly) trying to avoid.
    /// </summary>
    [Fact]
    public void PositionedSingleModel_WithoutBedGeometry_FallsBackToAutoArrange()
    {
        PlacementPlan plan = PlanPlacement(
            PositionedTransform,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: false);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.ArrangeFlag.Should().Be("--arrange 1");
        plan.PositionDropped.Should().BeTrue();

        string args = ComposeForPlan(plan, ProjectThreeMf, ModelStl);
        args.Should().NotContain("--center");
        args.Should().EndWith("\"/work/DumpTruck.stl\"", "no 3MF project could be built");
    }

    #endregion

    #region No regression for the origin / auto-arrange path

    [Fact]
    public void SingleModelAtOrigin_KeepsAutoArrange()
    {
        PlacementPlan plan = PlanPlacement(
            OriginTransform,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.ArrangeFlag.Should().Be("--arrange 1");
        plan.PositionDropped.Should().BeFalse();

        ComposeForPlan(plan, ProjectThreeMf, ModelStl).Should().Be(
            "--slice 0 --arrange 1 --ensure-on-bed " +
            "--load-settings \"/work/machine.json;/work/process.json\" " +
            "--load-filaments \"/work/filament.json\" " +
            "--allow-newer-file --outputdir \"/work/output\" \"/work/DumpTruck.stl\"");
    }

    [Fact]
    public void SingleModelWithRotationOnly_KeepsCliFlagsAndAutoArrange()
    {
        string rotated = """{"rotation":[1.5707963,0,0],"scale":[2,2,2],"position":[0,0,0]}""";

        PlacementPlan plan = PlanPlacement(
            rotated,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);

        string args = ComposeForPlan(plan, ProjectThreeMf, ModelStl);
        args.Should().Contain("--rotate-x 90.00");
        args.Should().Contain("--scale 2.0000");
        args.Should().Contain("--arrange 1");
        args.Should().NotContain("--center");
    }

    [Fact]
    public void NoTransform_KeepsAutoArrangeWithNoTransformFlags()
    {
        PlacementPlan plan = PlanPlacement(
            modelTransformJson: null,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.ArrangeFlag.Should().Be("--arrange 1");
        plan.TransformFlags.Should().BeEmpty();
    }

    #endregion

    #region Multi-model / multi-plate

    [Fact]
    public void MultipleModelsWithTransforms_UseThreeMfProject()
    {
        PlacementPlan plan = PlanPlacement(
            modelTransformJson: null,
            modelFileTransforms: [OriginTransform, PositionedTransform],
            modelPaths: ["/work/a.stl", "/work/b.stl"],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.ThreeMfProject);
        plan.ModelTransforms.Should().HaveCount(2);
        plan.ModelTransforms[0].Should().Be(OriginTransform);
        plan.ModelTransforms[1].Should().Be(PositionedTransform);

        // The 3MF replaces every input, so the command line carries exactly one model.
        ComposeForPlan(plan, ProjectThreeMf, "/work/a.stl", "/work/b.stl")
            .Should().NotContain("--load \"");
    }

    [Fact]
    public void MultipleModelsAtOrigin_WithoutSecondaryTransforms_AutoArranges()
    {
        PlacementPlan plan = PlanPlacement(
            modelTransformJson: null,
            modelFileTransforms: [OriginTransform, null],
            modelPaths: ["/work/a.stl", "/work/b.stl"],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);

        string args = ComposeForPlan(plan, ProjectThreeMf, "/work/a.stl", "/work/b.stl");
        args.Should().Contain("--load \"/work/b.stl\"");
        args.Should().EndWith("\"/work/a.stl\"");
        args.Should().NotContain("--center");
    }

    [Fact]
    public void PlateFlagAndPipeFlag_ArePlacedBeforeLoadSettings()
    {
        string args = BuildOrcaSlicerArguments(
            "--arrange 0",
            string.Empty,
            " --pipe \"/work/progress.pipe\"",
            " --plate 2",
            MachineJson,
            ProcessJson,
            FilamentJson,
            OutputDir,
            [ProjectThreeMf]);

        args.Should().Be(
            "--slice 0 --arrange 0 --ensure-on-bed --pipe \"/work/progress.pipe\" --plate 2 " +
            "--load-settings \"/work/machine.json;/work/process.json\" " +
            "--load-filaments \"/work/filament.json\" " +
            "--allow-newer-file --outputdir \"/work/output\" \"/work/project.3mf\"");
        args.Should().NotContain("--center");
    }

    #endregion

    #region Non-STL inputs

    [Fact]
    public void PositionedThreeMfInput_KeepsSourcePlacementWithoutCenterFlag()
    {
        PlacementPlan plan = PlanPlacement(
            PositionedTransform,
            modelFileTransforms: null,
            modelPaths: ["/work/model.3mf"],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.SourcePlacement);
        plan.ArrangeFlag.Should().Be("--arrange 0");
        plan.PositionDropped.Should().BeTrue();

        ComposeForPlan(plan, ProjectThreeMf, "/work/model.3mf").Should().NotContain("--center");
    }

    /// <summary>
    /// Only 3MF stores its own bed placement. OBJ/PLY/STEP/STP load at raw mesh or CAD
    /// coordinates, so <c>--arrange 0</c> would strand them wherever the file happens to sit —
    /// off the bed, tripping OrcaSlicer's CLI_OBJECTS_PARTLY_INSIDE check.
    /// </summary>
    [Theory]
    [InlineData("/work/model.obj")]
    [InlineData("/work/model.ply")]
    [InlineData("/work/model.step")]
    [InlineData("/work/model.stp")]
    public void PositionedFormatWithoutOwnPlacement_AutoArranges(string modelPath)
    {
        PlacementPlan plan = PlanPlacement(
            PositionedTransform,
            modelFileTransforms: null,
            modelPaths: [modelPath],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.ArrangeFlag.Should().Be("--arrange 1");
        plan.PositionDropped.Should().BeTrue();
    }

    /// <summary>
    /// A mixed STL + 3MF job cannot be re-meshed (not all STL) and does not uniformly carry its
    /// own placement (not all 3MF), so it must auto-arrange. Routing it to <c>--arrange 0</c>
    /// would leave the STL half at raw mesh coordinates.
    /// </summary>
    [Fact]
    public void MixedStlAndThreeMfInputs_AutoArrange()
    {
        PlacementPlan plan = PlanPlacement(
            modelTransformJson: null,
            modelFileTransforms: [null, """{"rotation":[0,0,1.5707963],"scale":[1,1,1],"position":[0,0,0]}"""],
            modelPaths: ["/work/a.stl", "/work/b.3mf"],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.ArrangeFlag.Should().Be("--arrange 1");
    }

    #endregion

    #region Runtime downgrade

    /// <summary>
    /// When the 3MF project cannot be built (for example an ASCII STL, or one over the triangle
    /// budget), the plan is rewritten to auto-arrange. Rotation and scale must be recovered as
    /// CLI flags, the layout is recorded as dropped, and no positional flag may appear.
    /// </summary>
    [Fact]
    public void DowngradeToAutoArrange_RecoversRotationAndScaleFlags()
    {
        string rotatedAndPositioned =
            """{"rotation":[1.5707963,0,0],"scale":[2,2,2],"position":[30,0,0]}""";

        PlacementPlan plan = PlanPlacement(
            rotatedAndPositioned,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);
        plan.Strategy.Should().Be(PlacementStrategy.ThreeMfProject);

        PlacementPlan downgraded = DowngradeToAutoArrange(plan);

        downgraded.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        downgraded.ArrangeFlag.Should().Be("--arrange 1");
        downgraded.PositionDropped.Should().BeTrue();
        downgraded.TransformFlags.Should().Contain("--rotate-x 90.00");
        downgraded.TransformFlags.Should().Contain("--scale 2.0000");
        downgraded.TransformFlags.Should().NotContain("--center");

        // The downgraded plan keeps the original STL, not the project that failed to build.
        Compose(downgraded, ModelStl).Should().EndWith("\"/work/DumpTruck.stl\"");
    }

    [Fact]
    public void DowngradeToAutoArrange_NoTransforms_ProducesNoFlags()
    {
        PlacementPlan plan = PlanPlacement(
            modelTransformJson: null,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        PlacementPlan downgraded = DowngradeToAutoArrange(plan);

        downgraded.TransformFlags.Should().BeEmpty();
        downgraded.ArrangeFlag.Should().Be("--arrange 1");
    }

    #endregion

    #region Non-uniform scale (#1799)

    /// <summary>The reproduction from issue #1799: X scaled 200%, Y and Z left at 100%, no
    /// custom position (model sits at the plate origin).</summary>
    private const string NonUniformScaleAtOrigin = """{"rotation":[0,0,0],"scale":[2,1,1],"position":[0,0,0]}""";

    /// <summary>
    /// A non-uniform scale needs embedding for the same reason a custom position does:
    /// OrcaSlicer 2.4.2's CLI <c>--scale</c> is a single value, so per-axis scale can only be
    /// expressed through the 3MF project matrix. This is the fix for the common case in #1799
    /// — a model at the plate origin no longer silently flattens to uniform scale.
    /// </summary>
    [Fact]
    public void NonUniformScale_SingleModelAtOrigin_UsesThreeMfProject()
    {
        PlacementPlan plan = PlanPlacement(
            NonUniformScaleAtOrigin,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.ThreeMfProject);
        plan.ArrangeFlag.Should().Be("--arrange 0");
        plan.TransformFlags.Should().BeEmpty("scale is baked into the 3MF matrix, per-axis and all");
        plan.PositionDropped.Should().BeFalse();
        plan.NonUniformScaleDropped.Should().BeFalse("the 3MF matrix honours per-axis scale, so nothing is dropped");
        plan.ModelTransforms.Should().ContainSingle().Which.Should().Be(NonUniformScaleAtOrigin);
    }

    /// <summary>
    /// When the 3MF path is unavailable (no bed centre), the non-uniform scale genuinely
    /// cannot be expressed on OrcaSlicer 2.4.2's CLI — <c>--scale</c> is a single value.
    /// <see cref="PlacementPlan.NonUniformScaleDropped"/> must record the degradation so the
    /// caller logs it rather than silently flattening the model (acceptance criteria in #1799).
    /// </summary>
    [Fact]
    public void NonUniformScale_WithoutBedGeometry_FallsBackToAutoArrangeAndReportsDrop()
    {
        PlacementPlan plan = PlanPlacement(
            NonUniformScaleAtOrigin,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: false);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.ArrangeFlag.Should().Be("--arrange 1");
        plan.NonUniformScaleDropped.Should().BeTrue();

        // The model was never given a custom position, so this fallback did not lose any
        // layout — only scale. PositionDropped must stay false here, or the caller logs a
        // misleading "requested layout could not be embedded" warning for a job that never
        // asked for one (issue #1799 review feedback).
        plan.PositionDropped.Should().BeFalse(
            "only scale, not position, triggered this fallback");

        // Best-effort isotropic approximation (scale[0]) still ships rather than nothing.
        string args = ComposeForPlan(plan, ProjectThreeMf, ModelStl);
        args.Should().Contain("--scale 2.0000");
        args.Should().NotContain("--center");
    }

    /// <summary>
    /// OBJ/PLY/STEP/STP cannot be re-meshed into a 3MF project (only STL can), so a
    /// non-uniform scale on one of these inputs must also be reported as dropped.
    /// </summary>
    [Fact]
    public void NonUniformScale_NonStlInput_FallsBackToAutoArrangeAndReportsDrop()
    {
        PlacementPlan plan = PlanPlacement(
            NonUniformScaleAtOrigin,
            modelFileTransforms: null,
            modelPaths: ["/work/model.obj"],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.NonUniformScaleDropped.Should().BeTrue();
    }

    /// <summary>
    /// A 3MF input already carries its own placement (<see cref="PlacementStrategy.SourcePlacement"/>),
    /// but there is no project being built here to bake a per-axis scale into, so the CLI
    /// flags still flatten it — and that must be reported.
    /// </summary>
    [Fact]
    public void NonUniformScale_ThreeMfInput_SourcePlacementReportsDrop()
    {
        PlacementPlan plan = PlanPlacement(
            NonUniformScaleAtOrigin,
            modelFileTransforms: null,
            modelPaths: ["/work/model.3mf"],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.SourcePlacement);
        plan.NonUniformScaleDropped.Should().BeTrue();
    }

    /// <summary>No regression for the uniform-scale case: it never needs embedding on its own
    /// and is never reported as dropped.</summary>
    [Fact]
    public void UniformScale_AtOrigin_KeepsAutoArrangeAndNeverReportsDrop()
    {
        string uniform = """{"rotation":[0,0,0],"scale":[2,2,2],"position":[0,0,0]}""";

        PlacementPlan plan = PlanPlacement(
            uniform,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);

        plan.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        plan.NonUniformScaleDropped.Should().BeFalse();

        string args = ComposeForPlan(plan, ProjectThreeMf, ModelStl);
        args.Should().Contain("--scale 2.0000");
    }

    /// <summary>
    /// When a 3MF project cannot be built at runtime, downgrading to auto-arrange must also
    /// recover — and report — a non-uniform scale as dropped, alongside the existing position
    /// drop.
    /// </summary>
    [Fact]
    public void DowngradeToAutoArrange_NonUniformScale_ReportsDrop()
    {
        string nonUniformAndPositioned = """{"rotation":[0,0,0],"scale":[2,1,1],"position":[30,0,0]}""";

        PlacementPlan plan = PlanPlacement(
            nonUniformAndPositioned,
            modelFileTransforms: null,
            modelPaths: [ModelStl],
            bedCenterKnown: true);
        plan.Strategy.Should().Be(PlacementStrategy.ThreeMfProject);

        PlacementPlan downgraded = DowngradeToAutoArrange(plan);

        downgraded.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        downgraded.PositionDropped.Should().BeTrue();
        downgraded.NonUniformScaleDropped.Should().BeTrue();
        downgraded.TransformFlags.Should().Contain("--scale 2.0000");
    }

    /// <summary>
    /// A secondary model's non-uniform scale must still be reported as dropped on downgrade,
    /// even though the CLI can only ever carry the *primary* model's flags — the drop
    /// detection must not silently ignore anything but the first model in the job (issue
    /// #1799 review feedback).
    /// </summary>
    [Fact]
    public void DowngradeToAutoArrange_SecondaryModelNonUniformScale_StillReportsDrop()
    {
        string uniformPrimary = """{"rotation":[0,0,0],"scale":[1,1,1],"position":[0,0,0]}""";
        string nonUniformSecondary = """{"rotation":[0,0,0],"scale":[2,1,1],"position":[0,0,0]}""";

        PlacementPlan plan = PlanPlacement(
            modelTransformJson: null,
            modelFileTransforms: [uniformPrimary, nonUniformSecondary],
            modelPaths: [ModelStl, "/work/model2.stl"],
            bedCenterKnown: true);
        plan.Strategy.Should().Be(PlacementStrategy.ThreeMfProject);

        PlacementPlan downgraded = DowngradeToAutoArrange(plan);

        downgraded.Strategy.Should().Be(PlacementStrategy.AutoArrange);
        downgraded.NonUniformScaleDropped.Should().BeTrue(
            "the secondary model's non-uniform scale is lost by this downgrade too, even though " +
            "only the primary model's flags ever reach the CLI");

        // Unchanged, pre-existing CLI limitation: only the primary (uniform) model's flags are
        // recoverable, so no --scale flag beyond the default is expected here.
        downgraded.TransformFlags.Should().NotContain("--scale 2.0000");
    }

    #endregion

    #region Placement warning descriptions (#1799)

    /// <summary>
    /// A plan that embedded everything into a 3MF project dropped nothing, so no warning
    /// should be produced.
    /// </summary>
    [Fact]
    public void DescribePlacementWarnings_ThreeMfProject_ReturnsNoWarnings()
    {
        var plan = new PlacementPlan(PlacementStrategy.ThreeMfProject, "--arrange 0", string.Empty, [NonUniformScaleAtOrigin], false, false);

        IReadOnlyList<PlacementWarningKind> warnings = DescribePlacementWarnings(plan);

        warnings.Should().BeEmpty();
    }

    /// <summary>
    /// SourcePlacement always warns that the layout could not be re-embedded, regardless of
    /// scale — this is the only strategy where the warning fires unconditionally.
    /// </summary>
    [Fact]
    public void DescribePlacementWarnings_SourcePlacement_WarnsAboutLayout()
    {
        var plan = new PlacementPlan(PlacementStrategy.SourcePlacement, "--arrange 0", string.Empty, ["/work/model.3mf"], true, false);

        IReadOnlyList<PlacementWarningKind> warnings = DescribePlacementWarnings(plan);

        warnings.Should().ContainSingle().Which.Should().Be(PlacementWarningKind.SourcePlacementFallback);
    }

    /// <summary>
    /// SourcePlacement with a dropped non-uniform scale must warn about both the layout and
    /// the scale degradation — proving the caller actually surfaces both, not just one
    /// (acceptance criteria in #1799: degradation must be logged).
    /// </summary>
    [Fact]
    public void DescribePlacementWarnings_SourcePlacementWithNonUniformScale_WarnsAboutBoth()
    {
        var plan = new PlacementPlan(PlacementStrategy.SourcePlacement, "--arrange 0", "--scale 2.0000", ["/work/model.3mf"], true, true);

        IReadOnlyList<PlacementWarningKind> warnings = DescribePlacementWarnings(plan);

        warnings.Should().BeEquivalentTo(
            [PlacementWarningKind.SourcePlacementFallback, PlacementWarningKind.NonUniformScaleFlattened]);
    }

    /// <summary>
    /// AutoArrange with a dropped position (but uniform scale) must warn only about the
    /// layout, not scale — proving the two concerns are reported independently.
    /// </summary>
    [Fact]
    public void DescribePlacementWarnings_AutoArrangePositionDroppedOnly_WarnsAboutLayoutOnly()
    {
        var plan = new PlacementPlan(PlacementStrategy.AutoArrange, "--arrange 1", string.Empty, [ModelStl], true, false);

        IReadOnlyList<PlacementWarningKind> warnings = DescribePlacementWarnings(plan);

        warnings.Should().ContainSingle().Which.Should().Be(PlacementWarningKind.LayoutNotEmbedded);
    }

    /// <summary>
    /// AutoArrange with a dropped non-uniform scale but no position drop must warn only about
    /// scale — this is the exact scenario Bishop flagged: a scale-only fallback must not also
    /// claim the layout was lost.
    /// </summary>
    [Fact]
    public void DescribePlacementWarnings_AutoArrangeNonUniformScaleOnly_WarnsAboutScaleOnly()
    {
        var plan = new PlacementPlan(PlacementStrategy.AutoArrange, "--arrange 1", "--scale 2.0000", [NonUniformScaleAtOrigin], false, true);

        IReadOnlyList<PlacementWarningKind> warnings = DescribePlacementWarnings(plan);

        warnings.Should().ContainSingle().Which.Should().Be(PlacementWarningKind.NonUniformScaleFlattened);
    }

    /// <summary>
    /// AutoArrange with neither drop (e.g. no transform requested at all) must produce no
    /// warnings.
    /// </summary>
    [Fact]
    public void DescribePlacementWarnings_AutoArrangeNoDrops_ReturnsNoWarnings()
    {
        var plan = new PlacementPlan(PlacementStrategy.AutoArrange, "--arrange 1", string.Empty, [null], false, false);

        IReadOnlyList<PlacementWarningKind> warnings = DescribePlacementWarnings(plan);

        warnings.Should().BeEmpty();
    }

    #endregion

    #region Guards

    [Fact]
    public void BuildOrcaSlicerArguments_NoModels_Throws()
    {
        Action act = () => BuildOrcaSlicerArguments(
            "--arrange 1", string.Empty, string.Empty, string.Empty,
            MachineJson, ProcessJson, FilamentJson, OutputDir, []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PlanPlacement_NullModelPaths_Throws()
    {
        Action act = () => PlanPlacement(null, null, null!, true);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Bed centre

    [Theory]
    [InlineData("""{"printable_area":["0x0","250x0","250x250","0x250"]}""", 125, 125)]
    [InlineData("""{"printable_area":"0x0,220x0,220x220,0x220"}""", 110, 110)]
    [InlineData("""{"printable_area":["50x30","300x30","300x300","50x300"]}""", 175, 165)]
    [InlineData("""{"printable_area":["-100x-100","100x-100","100x100","-100x100"]}""", 0, 0)]
    public void TryReadBedCenter_ReturnsPrintableAreaBoundingBoxCenter(string json, double x, double y)
    {
        (double X, double Y)? center = TryReadBedCenter(json);

        center.Should().NotBeNull();
        center!.Value.X.Should().BeApproximately(x, 1e-9);
        center.Value.Y.Should().BeApproximately(y, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json{")]
    [InlineData("""{"nozzle_diameter":["0.4"]}""")]
    [InlineData("""{"printable_area":[]}""")]
    [InlineData("""{"printable_area":["not-a-point","also,bad"]}""")]
    public void TryReadBedCenter_UnusableProfile_ReturnsNull(string? json)
    {
        TryReadBedCenter(json).Should().BeNull();
    }

    #endregion
}

