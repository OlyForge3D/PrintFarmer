using Xunit;

// Enable parallel test execution at the assembly level.
// Unit tests will run in parallel for speed.
// Integration tests using SQLite should use [Collection(IntegrationTestCollection.Name)]
// to run sequentially and avoid disk I/O conflicts.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerClass, MaxParallelThreads = 4)]
