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
/// both classes in one DisableParallelization collection is the narrowest fix available from
/// test code alone: it guarantees neither class's tests execute concurrently with the other's,
/// while leaving both fully free to run in parallel with every other collection in the assembly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class RateLimiterEnvSerialCollection
{
    /// <summary>Collection name constant for consistent referencing across test classes.</summary>
    public const string Name = "RateLimiterEnv Serial Tests";
}
