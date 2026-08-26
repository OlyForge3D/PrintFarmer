using Xunit;

// Run test collections/classes in parallel. Each integration test class either owns its own
// isolated named in-memory SQLite database (per-class CustomWebApplicationFactory via
// IClassFixture) or shares one via IntegrationTestCollection — in both cases the database is
// private to that class/collection, so there are no cross-class disk I/O conflicts to guard
// against. MaxParallelThreads is left unset so xUnit uses its default (processor-count-based)
// degree of parallelism; xunit.runner.json controls the actual thread cap.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass)]
