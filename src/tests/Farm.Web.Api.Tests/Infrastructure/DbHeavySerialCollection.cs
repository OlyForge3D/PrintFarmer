using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Serializes execution of all database-heavy integration tests to prevent parallel SQLite and
/// WebApplicationFactory contention. Apply via [Collection("DbHeavySerial")].
/// </summary>
[CollectionDefinition("DbHeavySerial", DisableParallelization = true)]
public sealed class DbHeavySerialCollection { }
