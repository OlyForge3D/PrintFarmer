using Xunit;

namespace Farm.Slicer.Module.Tests.TestInfrastructure;

/// <summary>
/// Collection definition for SQLite integration tests that need sequential execution.
/// Tests in this collection will run one at a time to avoid SQLite disk I/O conflicts,
/// while unit tests (not in this collection) can run in parallel.
/// </summary>
/// <remarks>
/// Usage: Add [Collection(IntegrationTestCollection.Name)] to test classes that use
/// CustomWebApplicationFactory or access SQLite databases.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class IntegrationTestCollection : ICollectionFixture<CustomWebApplicationFactory>
{
    /// <summary>
    /// Collection name constant for consistent referencing across test classes.
    /// </summary>
    public const string Name = "Integration Tests";
}
