using Farm.Infrastructure.Services.Idempotency;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Idempotency;

/// <summary>
/// Deterministic unit tests for <see cref="IdempotencyKeyUtilities"/>. These
/// guarantees are the contract that the resource filter and the store rely on
/// for cross-endpoint isolation and header-smuggling resistance.
/// </summary>
public class IdempotencyKeyUtilitiesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("~!@#$%^&*()_+-=<>?")]
    public void IsValidKey_AcceptsPrintableAscii(string key)
        => IdempotencyKeyUtilities.IsValidKey(key).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("emoji \u2603")]
    [InlineData("has\r\nnewline")]
    public void IsValidKey_RejectsMalformed(string key)
        => IdempotencyKeyUtilities.IsValidKey(key).Should().BeFalse();

    [Fact]
    public void IsValidKey_RejectsNull()
        => IdempotencyKeyUtilities.IsValidKey(null).Should().BeFalse();

    [Fact]
    public void IsValidKey_RejectsOverLimit()
    {
        string over = new string('a', IdempotencyKeyUtilities.MaxKeyLength + 1);
        _ = IdempotencyKeyUtilities.IsValidKey(over).Should().BeFalse();
    }

    [Fact]
    public void ComputeRequestHash_IsDeterministic()
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}");
        string first = IdempotencyKeyUtilities.ComputeRequestHash(IdempotencyRouteKeys.TaskComplete, body);
        string second = IdempotencyKeyUtilities.ComputeRequestHash(IdempotencyRouteKeys.TaskComplete, body);
        _ = first.Should().Be(second).And.HaveLength(64);
    }

    [Fact]
    public void ComputeRequestHash_DiffersAcrossRoutes()
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}");
        string a = IdempotencyKeyUtilities.ComputeRequestHash(IdempotencyRouteKeys.TaskComplete, body);
        string b = IdempotencyKeyUtilities.ComputeRequestHash(IdempotencyRouteKeys.PartsInventoryAdjust, body);
        _ = a.Should().NotBe(b, "the same body against a different route must not collide");
    }

    [Fact]
    public void ComputeRequestHash_DiffersOnBody()
    {
        string a = IdempotencyKeyUtilities.ComputeRequestHash(
            IdempotencyRouteKeys.TaskComplete, System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"));
        string b = IdempotencyKeyUtilities.ComputeRequestHash(
            IdempotencyRouteKeys.TaskComplete, System.Text.Encoding.UTF8.GetBytes("{\"a\":2}"));
        _ = a.Should().NotBe(b);
    }

    [Fact]
    public void ComputeRequestHash_EmptyBodyStillProducesHexHash()
    {
        string hash = IdempotencyKeyUtilities.ComputeRequestHash(IdempotencyRouteKeys.TaskComplete, ReadOnlySpan<byte>.Empty);
        _ = hash.Should().HaveLength(64);
        _ = hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
            .Should().BeTrue("hex output must be lowercase and hex-only");
    }

    [Theory]
    [InlineData("idem:foo")]
    [InlineData("IDEM:foo")]                            // case-insensitive
    [InlineData("  idem:foo")]                          // trimmed before the check
    [InlineData("\uFF49\uFF44\uFF45\uFF4D:foo")]        // fullwidth ｉｄｅｍ: (NFKC → idem:)
    [InlineData("\uFF49\uFF44\uFF45\uFF4D\uFF1Afoo")]   // fullwidth ｉｄｅｍ： incl fullwidth colon
    [InlineData("\uFF49dem:foo")]                       // mixed-width ｉdem:
    // --- Hicks r7 blocker H2: the server-generated harvest: namespace is reserved too ---
    [InlineData("harvest:foo")]
    [InlineData("Harvest:FOO")]                         // case-insensitive
    [InlineData("  harvest:foo")]                       // trimmed before the check
    [InlineData("\uFF48\uFF41\uFF52\uFF56\uFF45\uFF53\uFF54\uFF1A\uFF46\uFF4F\uFF4F")] // fullwidth ｈａｒｖｅｓｔ：ｆｏｏ (NFKC → harvest:foo)
    public void IsReservedOperationKey_TrueForReservedNamespaceInAnyWidth(string operationKey)
        => IdempotencyKeyUtilities.IsReservedOperationKey(operationKey).Should().BeTrue(
            "the reserved 'idem:' and 'harvest:' namespaces must be detected regardless of case, width, or whitespace");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("myapp:idem:foo")]                      // reserved token not at the start
    [InlineData("idempotent")]                          // shares a stem, not the prefix
    [InlineData("harvestable-tote")]                    // shares a stem, not the 'harvest:' prefix
    [InlineData("myapp:harvest:foo")]                   // reserved token not at the start
    [InlineData("caf\u00E9-op")]                        // accented client key (precomposed)
    [InlineData("cafe\u0301-op")]                       // accented client key (decomposed)
    public void IsReservedOperationKey_FalseForEverythingElse(string? operationKey)
        => IdempotencyKeyUtilities.IsReservedOperationKey(operationKey).Should().BeFalse();
}
