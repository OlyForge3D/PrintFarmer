using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Attention source that surfaces enabled printers whose status cache says they are offline.
/// </summary>
/// <remarks>
/// <para>
/// A printer is considered <see cref="AttentionKind.Offline"/> when it is
/// <see cref="Farm.Infrastructure.Domain.Printer.IsEnabled"/> and either has no cached
/// status yet or the cached status reports <c>IsOnline == false</c>. Disabled printers
/// are hidden from operator listings and therefore excluded from the feed.
/// </para>
/// <para>
/// Severity is <see cref="AttentionSeverity.Warning"/>. Offline printers are actionable
/// but rarely block the whole shift; the failure source escalates when a job was in
/// progress.
/// </para>
/// </remarks>
public sealed class OfflineAttentionSource(
    IPrintersService printersService,
    IPrinterStatusCacheReader statusCache,
    TimeProvider? timeProvider = null) : IAttentionSource
{
    private readonly IPrintersService _printers =
        printersService ?? throw new ArgumentNullException(nameof(printersService));

    private readonly IPrinterStatusCacheReader _statusCache =
        statusCache ?? throw new ArgumentNullException(nameof(statusCache));

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public string SourceName => "offline";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        List<Printer> printers = await _printers.GetAllAsync(cancellationToken);
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        List<AttentionItemDto> items = new();

        foreach (Printer printer in printers)
        {
            if (!printer.IsEnabled)
            {
                continue;
            }

            PrinterStatusDto? status = _statusCache.GetStatus(printer.Id);
            bool isOffline = status is null || !status.IsOnline;
            if (!isOffline)
            {
                continue;
            }

            List<AttentionActionDto> actions = new(1)
            {
                new AttentionActionDto(AttentionActionKind.Snooze, "Snooze", RequiresConfirmation: false),
            };

            items.Add(new AttentionItemDto(
                Id: AttentionIdPrefixes.Build(AttentionIdPrefixes.Offline, printer.Id),
                Kind: AttentionKind.Offline,
                Severity: AttentionSeverity.Warning,
                PrinterId: printer.Id,
                PrinterName: printer.Name,
                Title: "Printer offline",
                Detail: $"{printer.Name} is not responding. Action: check power, network, and firmware, then re-scan.",
                OccurredAt: now,
                Actions: actions));
        }

        return items;
    }
}
