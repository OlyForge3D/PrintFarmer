using Farm.Modules.Printers.Services.Discovery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Farm.Modules.Printers.Tests.Security;

public sealed class DiscoveryServiceAuthenticatorTests
{
    [Fact]
    public void Authenticate_WithExactConfiguredKey_Succeeds()
    {
        DiscoveryServiceAuthenticator authenticator = CreateAuthenticator("expected-key");
        DefaultHttpContext context = new();
        context.Request.Headers["X-Discovery-Service-Key"] = "expected-key";

        bool result = authenticator.IsAuthorized(context.Request);

        Assert.True(result);
    }

    [Fact]
    public void Authenticate_WithDifferentKey_Fails()
    {
        DiscoveryServiceAuthenticator authenticator = CreateAuthenticator("expected-key");
        DefaultHttpContext context = new();
        context.Request.Headers["X-Discovery-Service-Key"] = "different-key";

        bool result = authenticator.IsAuthorized(context.Request);

        Assert.False(result);
    }

    [Fact]
    public void Authenticate_WithoutConfiguredKey_ReportsUnavailable()
    {
        DiscoveryServiceAuthenticator authenticator = CreateAuthenticator(sharedKey: null);

        bool result = authenticator.IsAuthorized(new DefaultHttpContext().Request);

        Assert.False(authenticator.IsConfigured);
        Assert.False(result);
    }

    private static DiscoveryServiceAuthenticator CreateAuthenticator(string? sharedKey)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DiscoveryAuth:SharedKey"] = sharedKey,
            })
            .Build();
        return new DiscoveryServiceAuthenticator(configuration);
    }
}
