using Farm.Infrastructure.Services.Idempotency;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Idempotency;

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
}
