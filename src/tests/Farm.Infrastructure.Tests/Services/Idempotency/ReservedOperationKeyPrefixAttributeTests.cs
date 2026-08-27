using Farm.Infrastructure.Services.Idempotency;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Idempotency;

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
    // --- Hicks r4 blocker 2: width/compatibility variants must not bypass the ordinal guard ---
    [InlineData("\uFF49\uFF44\uFF45\uFF4D:foo")]        // fullwidth ｉｄｅｍ: + ASCII colon
    [InlineData("\uFF49\uFF44\uFF45\uFF4D\uFF1Afoo")]   // fullwidth ｉｄｅｍ： + FULLWIDTH colon (U+FF1A)
    [InlineData("\uFF49dem:foo")]                       // mixed: fullwidth ｉ + ASCII dem:
    [InlineData("\uFF29\uFF24\uFF25\uFF2D:bar")]        // fullwidth uppercase ＩＤＥＭ: → NFKC IDEM:
    // --- Hicks r7 blocker H2: the reserved harvest: namespace must be rejected the same way ---
    [InlineData("harvest:foo")]
    [InlineData("Harvest:FOO")]                         // case-insensitive
    [InlineData("  harvest:leading-space")]             // surrounding whitespace trimmed
    [InlineData("\uFF48\uFF41\uFF52\uFF56\uFF45\uFF53\uFF54\uFF1A\uFF46\uFF4F\uFF4F")] // fullwidth ｈａｒｖｅｓｔ：ｆｏｏ → NFKC harvest:foo
    public void IsValid_RejectsReservedPrefix(string operationKey)
        => Attribute.IsValid(operationKey).Should().BeFalse(
            "the reserved 'idem:' and 'harvest:' prefixes must be rejected regardless of case, width, or surrounding whitespace");

    [Theory]
    [InlineData(null)]                    // optional field → valid
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("myapp:idem:foo")]        // reserved token not at the start → allowed
    [InlineData("idempotent")]            // shares a stem but is not the "idem:" prefix
    [InlineData("harvestable-tote")]      // shares a stem but is not the "harvest:" prefix
    [InlineData("op-1234")]
    // Accented client keys are legitimate and must pass in either Unicode composition form; NFKC is
    // used only to catch the reserved prefix, never to reject ordinary international characters.
    [InlineData("caf\u00E9-op")]          // precomposed é (U+00E9)
    [InlineData("cafe\u0301-op")]         // decomposed e + combining acute (U+0301)
    [InlineData("\uFF4Fp-1234")]          // fullwidth ｏp-1234 → NFKC op-1234, not reserved
    public void IsValid_AllowsEverythingElse(string? operationKey)
        => Attribute.IsValid(operationKey).Should().BeTrue();

    [Fact]
    public void FormatErrorMessage_NamesTheReservedPrefixes()
    {
        string message = Attribute.FormatErrorMessage("operationKey");
        _ = message.Should().Contain(IdempotencyKeyUtilities.SynthesizedOperationKeyPrefix);
        _ = message.Should().Contain(IdempotencyKeyUtilities.HarvestOperationKeyPrefix);
    }
}
