using Xunit;

namespace Farm.Slicer.Module.Tests.TestInfrastructure;

// ArtifactsMetrics (src/slicer/Farm.Slicer.Module/Services/Metrics/ArtifactsMetrics.cs) exposes a
// single process-wide static gauge (s_storageBytes) observed via a MeterListener that captures
// every measurement published on the shared "PrintFarmer.Artifacts" meter for the lifetime of the
// listener, not just measurements caused by the current test's own uploads.
//
// ArtifactsThresholdTests calls ArtifactsMetrics.ResetForTests() at the start of each of its Facts,
// zeroing that static state. If that reset races with ArtifactsMetricsTests reading/asserting on
// the same static gauge (e.g. Storage_Gauge_Reflects_Cumulative_Size measures a before/after delta
// across two uploads), the reset can land in the middle of the measurement window and corrupt the
// result in either direction.
//
// xUnit runs a [CollectionDefinition(DisableParallelization = true)] collection only after every
// parallel-capable collection in the assembly has already finished, one disabled collection at a
// time (confirmed against xUnit's assembly-runner scheduling: parallel collections run first and
// to completion; disabled collections then run serially, isolated from everything else). Putting
// both classes in this collection therefore guarantees not just that a ResetForTests() call can
// never interleave with an ArtifactsMetricsTests assertion window, but that NO other test in the
// suite is running (and therefore no other test can be uploading artifacts / touching these
// statics) while either class executes - so the exact-equality assertions in both classes remain
// valid, not just tolerant of noise.
//
// This collection is intentionally narrow (see IntegrationTestCollection.cs - do not add other
// classes here or reintroduce a project-wide mega-collection): only these two classes actually
// touch ArtifactsMetrics' static state in ways that require this isolation.
[CollectionDefinition(Name, DisableParallelization = true)]
public class ArtifactsMetricsSerialCollection
{
    public const string Name = "ArtifactsMetricsSerial";
}
