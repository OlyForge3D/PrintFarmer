using Xunit;

namespace Farm.Testing.Shared;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderDatabaseTestCollection
{
    public const string Name = "Provider database tests";
}
