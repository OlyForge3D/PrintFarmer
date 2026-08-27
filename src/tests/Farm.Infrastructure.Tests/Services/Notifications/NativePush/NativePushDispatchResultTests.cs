using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Notifications.NativePush;

public sealed class NativePushDispatchResultTests
{
    [Fact]
    public void Delivered_HasSuccessAndNoOtherFlags()
    {
        NativePushDispatchResult r = NativePushDispatchResult.Delivered();
        r.Success.Should().BeTrue();
        r.TokenInvalidated.Should().BeFalse();
        r.IsTransient.Should().BeFalse();
        r.Reason.Should().BeNull();
    }

    [Fact]
    public void Invalidated_MarksTokenInvalidated()
    {
        NativePushDispatchResult r = NativePushDispatchResult.Invalidated("BadDeviceToken");
        r.Success.Should().BeFalse();
        r.TokenInvalidated.Should().BeTrue();
        r.IsTransient.Should().BeFalse();
        r.Reason.Should().Be("BadDeviceToken");
    }

    [Fact]
    public void Transient_MarksIsTransient()
    {
        NativePushDispatchResult r = NativePushDispatchResult.Transient("http_503");
        r.Success.Should().BeFalse();
        r.IsTransient.Should().BeTrue();
        r.TokenInvalidated.Should().BeFalse();
        r.Reason.Should().Be("http_503");
    }

    [Fact]
    public void Terminal_IsNeitherTransientNorInvalidated()
    {
        NativePushDispatchResult r = NativePushDispatchResult.Terminal("http_400");
        r.Success.Should().BeFalse();
        r.IsTransient.Should().BeFalse();
        r.TokenInvalidated.Should().BeFalse();
        r.Reason.Should().Be("http_400");
    }

    [Fact]
    public void NotConfigured_IsTerminalWithReason()
    {
        NativePushDispatchResult r = NativePushDispatchResult.NotConfigured();
        r.Success.Should().BeFalse();
        r.IsTransient.Should().BeFalse();
        r.Reason.Should().Be("notConfigured");
    }
}
