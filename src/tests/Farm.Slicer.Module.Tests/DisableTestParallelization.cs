using Xunit;

// Enable parallel test execution at the assembly level.
// CollectionPerClass means every test class is its own collection by default, so different
// classes run in parallel with each other (bounded by MaxParallelThreads) while tests within a
// single class run sequentially. WebApplicationFactory-backed integration tests rely on that
// per-class sequencing for ResetDatabaseAsync-style state resets; see
// TestInfrastructure/IntegrationTestCollection.cs for why they no longer need a single shared,
// DisableParallelization=true collection across the whole suite.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, MaxParallelThreads = 4)]
