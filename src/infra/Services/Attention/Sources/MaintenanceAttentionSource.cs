using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Attention source that surfaces active maintenance alerts.
/// </summary>
/// <remarks>
/// Maintenance alerts store severity on a 1..4 scale
/// (<see cref="Farm.Infrastructure.Domain.MaintenanceAlert.Severity"/>). This source
/// maps 4 → <see cref="AttentionSeverity.Critical"/>, 3 → <see cref="AttentionSeverity.Warning"/>,
/// and 1..2 → <see cref="AttentionSeverity.Info"/>.
/// </remarks>
public sealed class MaintenanceAttentionSource(IMaintenanceAlertRepository alertRepository) : IAttentionSource
{
    private readonly IMaintenanceAlertRepository _alerts =
        alertRepository ?? throw new ArgumentNullException(nameof(alertRepository));

    /// <inheritdoc />
    public string SourceName => "maintenance";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        List<MaintenanceAlert> alerts = await _alerts.GetAllActiveAlertsAsync(cancellationToken);
        List<AttentionItemDto> items = new(alerts.Count);

        foreach (MaintenanceAlert alert in alerts)
        {
            AttentionSeverity severity = alert.Severity switch
            {
                >= 4 => AttentionSeverity.Critical,
                3 => AttentionSeverity.Warning,
                _ => AttentionSeverity.Info,
            };

            string printerName = alert.Printer?.Name ?? "Unknown printer";
            string title = string.IsNullOrWhiteSpace(alert.Title) ? "Maintenance due" : alert.Title;
            string detail = string.IsNullOrWhiteSpace(alert.Message)
                ? $"Maintenance is due on {printerName}. Action: complete or acknowledge the task."
                : $"{alert.Message} Action: complete or acknowledge the task.";

            List<AttentionActionDto> actions = new(4)
            {
                new AttentionActionDto(AttentionActionKind.Resolve, "Resolve", RequiresConfirmation: true),
                new AttentionActionDto(AttentionActionKind.Acknowledge, "Acknowledge", RequiresConfirmation: false),
                new AttentionActionDto(AttentionActionKind.Dismiss, "Dismiss", RequiresConfirmation: true),
                new AttentionActionDto(AttentionActionKind.Snooze, "Snooze", RequiresConfirmation: false),
            };

            items.Add(new AttentionItemDto(
                Id: AttentionIdPrefixes.Build(AttentionIdPrefixes.Maintenance, alert.Id),
                Kind: AttentionKind.Maintenance,
                Severity: severity,
                PrinterId: alert.PrinterId,
                PrinterName: printerName,
                Title: title,
                Detail: detail,
                OccurredAt: DateTime.SpecifyKind(alert.CreatedAt, DateTimeKind.Utc),
                Actions: actions));
        }

        return items;
    }
}
