using Xunit;

namespace Farm.Web.Api.Tests.TestInfrastructure;

/// <summary>
/// Collection definition sharing a single <see cref="CustomWebApplicationFactory"/> instance
/// (with default configuration) across test classes that opt in via
/// [Collection(IntegrationTestCollection.Name)]. Each factory already owns its own isolated,
/// named in-memory SQLite database, so members of this collection run in parallel with every
/// other collection like any other; this definition only exists to avoid building a redundant
/// default-configuration host per class that doesn't need its own overrides.
/// </summary>
/// <remarks>
/// Usage: Add [Collection(IntegrationTestCollection.Name)] to test classes that want to reuse
/// the shared default-configuration CustomWebApplicationFactory instead of declaring their own
/// IClassFixture&lt;Factory&gt;. Classes that need custom configuration overrides should use
/// their own nested Factory : CustomWebApplicationFactory with IClassFixture&lt;Factory&gt;
/// instead of this collection.
/// </remarks>
[CollectionDefinition(Name)]
public class IntegrationTestCollection : ICollectionFixture<CustomWebApplicationFactory>
{
    /// <summary>
    /// Collection name constant for consistent referencing across test classes.
    /// </summary>
    public const string Name = "Integration Tests";
}
