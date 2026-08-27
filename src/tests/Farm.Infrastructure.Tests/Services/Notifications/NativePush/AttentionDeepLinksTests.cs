using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Notifications.NativePush;

public sealed class AttentionDeepLinksTests
{
    private static readonly Guid PrinterId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid JobId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Scheme_IsPrintFarmer()
    {
        AttentionDeepLinks.Scheme.Should().Be("printfarmer");
    }

    [Fact]
    public void For_Failure_RoutesToAttentionItem()
    {
        AttentionDeepLinks.For(AttentionKind.Failure, PrinterId, "att-a", null, null)
            .Should().Be("printfarmer://attention/att-a");
    }

    [Fact]
    public void For_Maintenance_RoutesToAttentionItem()
    {
        AttentionDeepLinks.For(AttentionKind.Maintenance, PrinterId, "att-m", null, null)
            .Should().Be("printfarmer://attention/att-m");
    }

    [Fact]
    public void For_Harvest_RoutesToAttentionItem()
    {
        AttentionDeepLinks.For(AttentionKind.Harvest, PrinterId, "att-h", null, null)
            .Should().Be("printfarmer://attention/att-h");
    }

    [Fact]
    public void For_Offline_RoutesToPrinterDetail()
    {
        AttentionDeepLinks.For(AttentionKind.Offline, PrinterId, "att-o", null, null)
            .Should().Be($"printfarmer://printer/{PrinterId:D}");
    }

    [Fact]
    public void For_Runout_WithToolAndJob_RoutesToSwapWithJobId()
    {
        AttentionDeepLinks.For(AttentionKind.Runout, PrinterId, "att-r", 2, JobId)
            .Should().Be($"printfarmer://printer/{PrinterId:D}/swap/2?jobId={JobId:D}");
    }

    [Fact]
    public void For_Runout_WithoutJob_OmitsQueryString()
    {
        AttentionDeepLinks.For(AttentionKind.Runout, PrinterId, "att-r", 0, null)
            .Should().Be($"printfarmer://printer/{PrinterId:D}/swap/0");
    }

    [Fact]
    public void For_Runout_WithoutTool_DefaultsToZero()
    {
        AttentionDeepLinks.For(AttentionKind.Runout, PrinterId, "att-r", null, null)
            .Should().Be($"printfarmer://printer/{PrinterId:D}/swap/0");
    }
}
