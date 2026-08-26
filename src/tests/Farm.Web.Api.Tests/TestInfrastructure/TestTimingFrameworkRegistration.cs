using Xunit.Sdk;

// Assembly-level registration of the custom test framework (defined in Farm.Testing.Shared)
// that emits a timing summary. This attribute must live in the assembly xUnit actually
// executes tests from, so it stays here even though TimingReportingTestFramework itself now
// lives in Farm.Testing.Shared — see the remarks on that class.
[assembly: TestFramework("Farm.Testing.Shared.TimingReportingTestFramework", "Farm.Testing.Shared")]
