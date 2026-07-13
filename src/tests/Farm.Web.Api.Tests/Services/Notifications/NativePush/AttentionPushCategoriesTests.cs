using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications.NativePush;

/// <summary>
/// Locks the fixed category / action / thread-id matrix. Any change here is a wire-contract
/// change that must also update the mobile client and the shared #716 catalog.
/// </summary>
public sealed class AttentionPushCategoriesTests
{
    [Theory]
    [InlineData(AttentionKind.Failure, "PRINTER_FAILURE")]
    [InlineData(AttentionKind.Offline, "PRINTER_OFFLINE")]
    [InlineData(AttentionKind.Maintenance, "MAINTENANCE_DUE")]
    [InlineData(AttentionKind.Harvest, "HARVEST_READY")]
    [InlineData(AttentionKind.Runout, "FILAMENT_RUNOUT")]
    public void CategoryFor_ReturnsFixedCategory(AttentionKind kind, string expected)
    {
        AttentionPushCategories.CategoryFor(kind).Should().Be(expected);
    }

    [Fact]
    public void ActionsFor_Failure_YieldsPauseCancelSnooze()
    {
        AttentionPushCategories.ActionsFor(AttentionKind.Failure)
            .Should().Equal(AttentionPushCategories.ActionPause, AttentionPushCategories.ActionCancel, AttentionPushCategories.ActionSnooze15);
    }

    [Fact]
    public void ActionsFor_Offline_OnlySnooze()
    {
        AttentionPushCategories.ActionsFor(AttentionKind.Offline)
            .Should().Equal(AttentionPushCategories.ActionSnooze15);
    }

    [Fact]
    public void ActionsFor_Maintenance_AcknowledgeAndSnooze()
    {
        AttentionPushCategories.ActionsFor(AttentionKind.Maintenance)
            .Should().Equal(AttentionPushCategories.ActionAcknowledge, AttentionPushCategories.ActionSnooze15);
    }

    [Fact]
    public void ActionsFor_Harvest_IsTapOnly()
    {
        AttentionPushCategories.ActionsFor(AttentionKind.Harvest).Should().BeEmpty();
    }

    [Fact]
    public void ActionsFor_Runout_OpenSwapAndSnooze()
    {
        AttentionPushCategories.ActionsFor(AttentionKind.Runout)
            .Should().Equal(AttentionPushCategories.ActionOpenSwap, AttentionPushCategories.ActionSnooze15);
    }

    [Fact]
    public void ThreadIdFor_Failure_UsesPerPrinterThread()
    {
        Guid printer = Guid.Parse("11111111-2222-3333-4444-555555555555");
        AttentionPushCategories.ThreadIdFor(AttentionKind.Failure, printer, null, "item-1")
            .Should().Be($"printer:{printer:D}:failure");
    }

    [Fact]
    public void ThreadIdFor_Offline_UsesPerPrinterThread()
    {
        Guid printer = Guid.NewGuid();
        AttentionPushCategories.ThreadIdFor(AttentionKind.Offline, printer, null, "x")
            .Should().Be($"printer:{printer:D}:offline");
    }

    [Fact]
    public void ThreadIdFor_Runout_UsesPerToolheadThread()
    {
        Guid printer = Guid.NewGuid();
        AttentionPushCategories.ThreadIdFor(AttentionKind.Runout, printer, 2, "x")
            .Should().Be($"printer:{printer:D}:runout:2");
    }

    [Fact]
    public void ThreadIdFor_Harvest_UsesPerItemThread()
    {
        AttentionPushCategories.ThreadIdFor(AttentionKind.Harvest, Guid.NewGuid(), null, "att-9")
            .Should().Be("attention:att-9");
    }

    [Fact]
    public void ThreadIdFor_Maintenance_UsesPerItemThread()
    {
        AttentionPushCategories.ThreadIdFor(AttentionKind.Maintenance, Guid.NewGuid(), null, "att-10")
            .Should().Be("attention:att-10");
    }
}
