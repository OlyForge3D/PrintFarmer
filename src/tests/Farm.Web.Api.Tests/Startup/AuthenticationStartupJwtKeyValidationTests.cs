using Farm.Web.Api.Startup;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression coverage for <see cref="AuthenticationStartup.ValidateJwtKey"/>, the guard that
/// stops the API from starting outside <c>Development</c> with a JWT signing key that is
/// either a placeholder value shipped in this repository's deployment templates or shorter
/// than the documented minimum byte length. See issue #1294.
/// </summary>
public sealed class AuthenticationStartupJwtKeyValidationTests
{
    // Reads the denylisted value directly from AuthenticationStartup (internal, exposed via
    // InternalsVisibleTo) instead of re-declaring the literal here, per the issue's request
    // not to reproduce the placeholder anywhere beyond what already exists in the repo.
    private static readonly string ShippedPlaceholderKey = AuthenticationStartup.ShippedPlaceholderKeys[0];

    private const string ValidProductionKey = "ThisIsASufficientlyLongTestOnlyJwtSigningKey1234!";

    [Fact]
    public void ValidateJwtKey_ThrowsOutsideDevelopment_WhenKeyIsShippedPlaceholder()
    {
        Action act = () => AuthenticationStartup.ValidateJwtKey(ShippedPlaceholderKey, "Production");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*placeholder*");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ValidateJwtKey_ThrowsOutsideDevelopment_WhenKeyIsShorterThanMinimumByteLength(string environmentName)
    {
        string shortKey = new('a', AuthenticationStartup.MinimumKeyLengthBytes - 1);

        Action act = () => AuthenticationStartup.ValidateJwtKey(shortKey, environmentName);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bytes*");
    }

    [Fact]
    public void ValidateJwtKey_DoesNotThrow_WhenKeyMeetsMinimumByteLengthOutsideDevelopment()
    {
        Action act = () => AuthenticationStartup.ValidateJwtKey(ValidProductionKey, "Production");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateJwtKey_ThrowsInDevelopment_WhenKeyIsEmpty()
    {
        Action act = () => AuthenticationStartup.ValidateJwtKey(string.Empty, "Development");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public void ValidateJwtKey_DoesNotThrowInDevelopment_ForShippedPlaceholderOrShortKey()
    {
        // Development must keep starting normally with its existing dev config: neither the
        // placeholder denylist nor the minimum-length floor apply outside non-Development
        // environments, so local development is not broken by this guard.
        Action placeholderAct = () => AuthenticationStartup.ValidateJwtKey(ShippedPlaceholderKey, "Development");
        Action shortKeyAct = () => AuthenticationStartup.ValidateJwtKey("short", "Development");

        placeholderAct.Should().NotThrow();
        shortKeyAct.Should().NotThrow();
    }
}
