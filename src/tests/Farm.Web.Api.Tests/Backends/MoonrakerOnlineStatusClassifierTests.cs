using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

public sealed class MoonrakerOnlineStatusClassifierTests
{
    [Fact]
    public void ResolveKlippyReady_WhenWebhooksReady_ReturnsTrue()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {
              "webhooks": { "state": "ready" }
            }
            """);

        bool? result = MoonrakerOnlineStatusClassifier.ResolveKlippyReady(doc.RootElement);

        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("disconnected")]
    [InlineData("startup")]
    public void ResolveKlippyReady_WhenWebhooksNotReady_ReturnsFalse(string state)
    {
        using JsonDocument doc = JsonDocument.Parse($$"""
            {
              "webhooks": { "state": "{{state}}" }
            }
            """);

        bool? result = MoonrakerOnlineStatusClassifier.ResolveKlippyReady(doc.RootElement);

        result.Should().BeFalse();
    }

    [Fact]
    public void ResolveKlippyReady_WhenStatusHasPrinterObjectsWithoutWebhooks_ReturnsTrue()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {
              "toolhead": { "position": [1.0, 2.0, 3.0] },
              "print_stats": { "state": "standby" }
            }
            """);

        bool? result = MoonrakerOnlineStatusClassifier.ResolveKlippyReady(doc.RootElement);

        result.Should().BeTrue();
    }

    [Fact]
    public void ResolveKlippyReady_WhenStatusOnlyHasUnknownShape_ReturnsNull()
    {
        using JsonDocument doc = JsonDocument.Parse("{}");

        bool? result = MoonrakerOnlineStatusClassifier.ResolveKlippyReady(doc.RootElement);

        result.Should().BeNull();
    }
}
