// Integration test collection placeholder.
//
// This used to group ~44 WebApplicationFactory-backed test classes into a single
// [Collection(IntegrationTestCollection.Name)] with DisableParallelization = true, on the
// theory that SQLite disk I/O conflicts required strictly sequential execution across every
// test class in the suite. That justification stopped applying once CustomWebApplicationFactory
// switched to a uniquely-named in-memory SQLite database per factory instance (see the
// `_databaseCounter`-derived connection string in CustomWebApplicationFactory) — there is no
// shared file or shared cache-name for different test classes to contend over.
//
// Forcing the whole collection to run on one thread meant those ~44 classes (and, transitively,
// the ~280 test methods inside them) were serialized behind a single lane while only a handful of
// other threads (bounded by MaxParallelThreads in DisableTestParallelization.cs) ran everything
// else in parallel. That was the dominant driver of Farm.Slicer.Module.Tests wall-clock time
// (issue #2021). Removing the shared collection lets xUnit's default CollectionPerClass behavior
// take over: each test class becomes its own collection and runs in parallel with the others
// (bounded by MaxParallelThreads), while tests within a single class still run sequentially,
// which keeps ResetDatabaseAsync-based per-test isolation safe.
//
// This file is intentionally left as a placeholder to avoid accidental reintroduction of the
// mega-collection / DisableParallelization pattern. If a future test genuinely needs strict
// sequential execution against another test class (a real shared resource, not just "it uses
// SQLite"), introduce a small, narrowly-scoped collection for just that group — do not add
// classes back to a single project-wide collection.
