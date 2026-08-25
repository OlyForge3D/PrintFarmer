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
// result in either direction - a plain ">=" tolerance (see ArtifactsMetricsTests) cannot absorb a
// concurrent reset, only concurrent *additional* uploads from other, unrelated test classes.
//
// This collection therefore serializes ONLY these two classes against each other so a
// ResetForTests() call can never interleave with an ArtifactsMetricsTests assertion window, while
// still letting both run in parallel with the rest of the suite (see IntegrationTestCollection.cs -
// do not add other classes here; this is intentionally narrow, not a return to the old
// project-wide mega-collection).
[CollectionDefinition(Name, DisableParallelization = true)]
public class ArtifactsMetricsSerialCollection
{
    public const string Name = "ArtifactsMetricsSerial";
}
