using Xunit;

namespace Farm.Testing.Shared;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderDatabaseTestCollection
{
    public const string Name = "Provider database tests";

    // S1118: this class is a pure xUnit CollectionDefinition marker (never instantiated by
    // application code - xUnit discovers it via the attribute alone), so a private constructor
    // documents that intent without affecting xUnit's reflection-based discovery.
    private ProviderDatabaseTestCollection()
    {
    }
}
