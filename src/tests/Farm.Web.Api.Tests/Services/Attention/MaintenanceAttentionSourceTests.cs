using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Attention.Sources;
using Farm.Infrastructure.Services.OperatorFeatures;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

public sealed class MaintenanceAttentionSourceTests
{
    [Fact]
    public async Task GetItems_TogglingFeature_HidesAndRestoresToolheadAlert()
    {
        Guid printerId = Guid.NewGuid();
        MaintenanceAlert printerAlert = BuildAlert(printerId, toolheadId: null);
        MaintenanceAlert toolheadAlert = BuildAlert(printerId, Guid.NewGuid());
        List<MaintenanceAlert> storedAlerts = [printerAlert, toolheadAlert];
        Mock<IMaintenanceAlertRepository> repository = new();
        repository.Setup(r => r.GetAllActiveAlertsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedAlerts);
        bool enabled = true;
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(() => enabled);
        MaintenanceAttentionSource source = new(repository.Object, gate.Object);

        IReadOnlyList<AttentionItemDto> visible = await source.GetItemsAsync(CancellationToken.None);
        visible.Should().HaveCount(2);

        enabled = false;

        IReadOnlyList<AttentionItemDto> hidden = await source.GetItemsAsync(CancellationToken.None);
        hidden.Should().ContainSingle()
            .Which.Id.Should().Contain(printerAlert.Id.ToString("D"));
        storedAlerts.Should().HaveCount(2);

        enabled = true;

        (await source.GetItemsAsync(CancellationToken.None)).Should().HaveCount(2);
    }

    private static MaintenanceAlert BuildAlert(Guid printerId, Guid? toolheadId)
        => new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            ToolheadId = toolheadId,
            Severity = 3,
            Status = MaintenanceAlertStatus.Active,
            Title = "Maintenance due",
            CreatedAt = DateTime.UtcNow,
        };
}
