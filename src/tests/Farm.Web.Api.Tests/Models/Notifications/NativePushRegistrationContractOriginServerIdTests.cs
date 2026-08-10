using Farm.Infrastructure.Domain.Notifications;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Models.Notifications;

/// <summary>
/// Verifies <see cref="NativePushRegistrationContract.IsCanonicalOriginServerId"/>: only the
/// exact, lowercase, hyphenated (RFC 4122 "D") <see cref="Guid"/> string form is accepted.
/// See issue #1407.
/// </summary>
public sealed class NativePushRegistrationContractOriginServerIdTests
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
    public void IsCanonicalOriginServerId_CanonicalLowercaseUuid_ReturnsTrue(string value)
    {
        NativePushRegistrationContract.IsCanonicalOriginServerId(value).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("11111111-1111-1111-1111-111111111111 ")]
    [InlineData("11111111-1111-1111-1111-11111111111")]
    [InlineData("11111111111111111111111111111111111")]
    [InlineData("11111111-1111-1111-1111-1111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111X")]
    public void IsCanonicalOriginServerId_MalformedOrWrongLength_ReturnsFalse(string? value)
    {
        NativePushRegistrationContract.IsCanonicalOriginServerId(value).Should().BeFalse();
    }

    [Fact]
    public void IsCanonicalOriginServerId_UppercaseGuid_ReturnsFalse()
    {
        NativePushRegistrationContract.IsCanonicalOriginServerId("ffffffff-ffff-ffff-ffff-ffffffffffff".ToUpperInvariant())
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("(11111111-1111-1111-1111-111111111111)")]
    public void IsCanonicalOriginServerId_NonDashedOrBracedForm_ReturnsFalse(string value)
    {
        NativePushRegistrationContract.IsCanonicalOriginServerId(value).Should().BeFalse();
    }

    [Fact]
    public void IsCanonicalOriginServerId_NotAGuidAtAll_ReturnsFalse()
    {
        NativePushRegistrationContract.IsCanonicalOriginServerId("not-a-guid-not-a-guid-not-a-guid-xx")
            .Should().BeFalse();
    }
}
