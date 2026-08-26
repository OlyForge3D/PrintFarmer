using Xunit;

namespace Farm.Web.Api.Tests.TestInfrastructure;

/// <summary>
/// Serializes test classes that would otherwise race on the process-wide
/// <c>TEST_DISABLE_RATE_LIMITER</c> environment variable read by
/// <see cref="Farm.Web.Api.Middleware.AuthenticationRateLimitMiddleware"/>.
/// <see cref="Farm.Web.Api.Tests.Middleware.AuthenticationRateLimitMiddlewareTests"/> flips that
/// variable to "true" for the duration of one test (resetting it in a finally block), while
/// <see cref="Farm.Web.Api.Tests.Integration.DesktopApiKeyExchangePolicyIntegrationTests"/>'s
/// RepeatedExchangeAttempts_ExceedingLimit_AreRateLimited test asserts the limiter is actually
/// enforced (a real 429) against a real host. Neither class shares a database, host, or any
/// other state with the other — the environment variable is the ONLY thing they share, and it
/// is process-wide rather than scoped to either class's factory, so no per-class isolation
/// (IClassFixture, a private in-memory database, etc.) can prevent the race on its own. Placing
/// both classes in the same named collection is the narrowest fix available from test code
/// alone: xUnit's <c>CollectionPerClass</c> assembly behavior (see
/// <c>DisableTestParallelization.cs</c>) already guarantees that test classes sharing one
/// explicit collection never run concurrently with each other, with no need for
/// <c>DisableParallelization</c> on the definition itself — that flag would instead serialize
/// this collection against every OTHER collection in the assembly too, reintroducing an
/// assembly-wide parallelism cap this PR exists to remove. Omitting it keeps both classes free
/// to run in parallel with every other collection while still never overlapping each other.
/// </summary>
[CollectionDefinition(Name)]
public class RateLimiterEnvSerialCollection
{
    /// <summary>Collection name constant for consistent referencing across test classes.</summary>
    public const string Name = "RateLimiterEnv Serial Tests";
}
