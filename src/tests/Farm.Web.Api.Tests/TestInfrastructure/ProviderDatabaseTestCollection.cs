using Xunit;

namespace Farm.Web.Api.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderDatabaseTestCollection
{
    public const string Name = "Provider database tests";
}
