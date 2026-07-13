using Farm.Infrastructure.Services.Idempotency;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Idempotency;

/// <summary>
/// Unit tests for <see cref="ReservedOperationKeyPrefixAttribute"/> (issue #715, Hicks r3
/// blocker 2). The attribute is the API-boundary half of the reserved-namespace guard: it makes
/// <c>[ApiController]</c> reject a client operationKey in the reserved <c>idem:</c> namespace with
/// a <c>400</c> ProblemDetails before the action runs. The service layer enforces the same rule
/// as defense-in-depth (see PartInventoryServiceTests).
/// </summary>
public class ReservedOperationKeyPrefixAttributeTests
{
    private static readonly ReservedOperationKeyPrefixAttribute Attribute = new();

    [Theory]
    [InlineData("idem:foo")]
    [InlineData("Idem:Foo")]              // case-insensitive
    [InlineData("IDEM:bar")]
    [InlineData("  idem:leading-space")]  // surrounding whitespace is trimmed before the check
    public void IsValid_RejectsReservedPrefix(string operationKey)
        => Attribute.IsValid(operationKey).Should().BeFalse(
            "the reserved 'idem:' prefix must be rejected regardless of case or surrounding whitespace");

    [Theory]
    [InlineData(null)]                    // optional field → valid
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("myapp:idem:foo")]        // reserved token not at the start → allowed
    [InlineData("idempotent")]            // shares a stem but is not the "idem:" prefix
    [InlineData("op-1234")]
    public void IsValid_AllowsEverythingElse(string? operationKey)
        => Attribute.IsValid(operationKey).Should().BeTrue();

    [Fact]
    public void FormatErrorMessage_NamesTheReservedPrefix()
        => Attribute.FormatErrorMessage("operationKey")
            .Should().Contain(IdempotencyKeyUtilities.SynthesizedOperationKeyPrefix);
}
