using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Infrastructure.Services.Catalog;

/// <summary>
/// Background service that periodically checks whether any printer's linked catalog
/// model has been updated since the printer last had its template applied.
/// When drift is detected, in-app notifications are created for all active users,
/// and — if <see cref="CatalogUpdateSettings.AutoApply"/> is enabled — the template
/// is applied automatically.
/// </summary>
public class CatalogUpdateDetectionService(
    IServiceProvider serviceProvider,
    ILogger<CatalogUpdateDetectionService> logger,
    IOptionsMonitor<CatalogUpdateSettings> settingsMonitor,
    IBackgroundServiceMonitor serviceMonitor) : BackgroundService
{
    private const string ServiceId = "CatalogUpdateDetectionService";

    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ILogger<CatalogUpdateDetectionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptionsMonitor<CatalogUpdateSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly IBackgroundServiceMonitor _serviceMonitor = serviceMonitor ?? throw new ArgumentNullException(nameof(serviceMonitor));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CatalogUpdateSettings settings = _settingsMonitor.CurrentValue;

        _serviceMonitor.Register(
            ServiceId,
            "Catalog Update Detection",
            "Scans printers for available catalog model template updates and notifies users",
            "Catalog",
            "pf-icon-refresh",
            settings.IntervalSeconds);
        _serviceMonitor.ReportStarted(ServiceId);

        if (!settings.Enabled)
        {
            _logger.LogInformation("Catalog update detection service is disabled");
            _serviceMonitor.ReportEnabled(ServiceId, false);
            return;
        }

        _serviceMonitor.ReportEnabled(ServiceId, true);
        _logger.LogInformation(
            "Catalog update detection service started. Interval: {Interval}s, AutoApply: {AutoApply}",
            settings.IntervalSeconds,
            settings.AutoApply);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                settings = _settingsMonitor.CurrentValue;
                if (!settings.Enabled)
                {
                    _logger.LogInformation("Catalog update detection disabled, pausing");
                    _serviceMonitor.ReportEnabled(ServiceId, false);
                    continue;
                }

                _serviceMonitor.ReportEnabled(ServiceId, true);
                await DetectAndHandleUpdatesAsync(settings, stoppingToken);
                _serviceMonitor.ReportSuccess(ServiceId, settings.IntervalSeconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Catalog update detection service stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in catalog update detection service");
                _serviceMonitor.ReportError(ServiceId, ex.Message);
            }
        }

        _serviceMonitor.ReportStopped(ServiceId);
    }

    private async Task DetectAndHandleUpdatesAsync(CatalogUpdateSettings settings, CancellationToken ct)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPrintersService printersService = scope.ServiceProvider.GetRequiredService<IPrintersService>();
        IUsersRepository usersRepository = scope.ServiceProvider.GetRequiredService<IUsersRepository>();

        // Load all enabled printers that have a real model assigned
        List<Printer> printers = await db.Printers
            .AsNoTracking()
            .Include(p => p.Model)
            .Include(p => p.Toolheads)
            .Where(p => p.IsEnabled && p.Model != null)
            .ToListAsync(ct);

        // Identify printers whose model has been updated since last sync
        List<Printer> outdated = printers
            .Where(p => p.Model != null && p.ServiceState != null && p.Model.UpdatedAt > (p.ServiceState.LastModelSyncAt ?? DateTime.MinValue))
            .ToList();

        if (outdated.Count == 0)
        {
            _logger.LogDebug("Catalog update detection: all {Total} printers are up to date", printers.Count);
            return;
        }

        _logger.LogInformation(
            "Catalog update detection: {Count}/{Total} printer(s) have catalog model updates available",
            outdated.Count,
            printers.Count);

        if (settings.AutoApply)
        {
            await AutoApplyUpdatesAsync(db, printersService, outdated, ct);
        }
        else
        {
            await NotifyUsersAsync(db, usersRepository, outdated, ct);
        }
    }

    /// <summary>
    /// Automatically applies the latest catalog model template to all outdated printers.
    /// </summary>
    private async Task AutoApplyUpdatesAsync(
        AppDbContext db,
        IPrintersService printersService,
        List<Printer> outdated,
        CancellationToken ct)
    {
        // Reload with tracking for write operations
        foreach (Printer readonly_p in outdated)
        {
            try
            {
                Printer? p = await db.Printers
                    .Include(p => p.Model)
                    .Include(p => p.Toolheads)
                    .FirstOrDefaultAsync(x => x.Id == readonly_p.Id, ct);

                if (p is null)
                {
                    continue;
                }

                bool applied = await printersService.ApplyModelTemplateAsync(p, forceOverwrite: false, ct);
                if (applied)
                {
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "[AutoApply] Applied catalog update to printer '{Name}' (model: '{Model}')",
                        p.Name, p.Model?.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AutoApply] Failed to apply catalog update to printer '{Name}'", readonly_p.Name);
            }
        }
    }

    /// <summary>
    /// Creates in-app notifications for active users about available catalog updates.
    /// Deduplicates: skips printers that already have an unread CatalogUpdateAvailable notification.
    /// </summary>
    private async Task NotifyUsersAsync(
        AppDbContext db,
        IUsersRepository usersRepository,
        List<Printer> outdated,
        CancellationToken ct)
    {
        // Collect IDs of printers that already have unread notifications (prevent spam)
        HashSet<string> alreadyNotifiedPrinterIds = await GetAlreadyNotifiedPrinterIdsAsync(db, ct);

        IReadOnlyList<Farm.Infrastructure.Contracts.Auth.UserDto> allUsers = await usersRepository.GetUsersAsync(ct);
        IEnumerable<Farm.Infrastructure.Contracts.Auth.UserDto> activeUsers = allUsers.Where(u => u.IsActive);

        foreach (Printer printer in outdated)
        {
            if (alreadyNotifiedPrinterIds.Contains(printer.Id.ToString()))
            {
                _logger.LogDebug(
                    "Skipping notification for printer '{Name}' — unread catalog update notification already exists",
                    printer.Name);
                continue;
            }

            string subject = $"Configuration update available: {printer.Name}";
            string body = $"The catalog model \"{printer.Model!.Name}\" has been updated. " +
                          $"Apply the latest template to \"{printer.Name}\" to get the newest configuration defaults.";

            string metadata = JsonSerializer.Serialize(new
            {
                printerId = printer.Id.ToString(),
                printerName = printer.Name,
                modelId = printer.ModelId.ToString(),
                modelName = printer.Model!.Name
            });

            foreach (Farm.Infrastructure.Contracts.Auth.UserDto user in activeUsers)
            {
                var notification = new Notification
                {
                    UserId = user.Id,
                    Type = NotificationType.CatalogUpdateAvailable,
                    Subject = subject,
                    Body = body,
                    Metadata = metadata,
                    CreatedAt = DateTime.UtcNow
                };

                db.Notifications.Add(notification);
            }

            _logger.LogInformation(
                "Created catalog update notifications for printer '{Name}' (model: '{Model}')",
                printer.Name, printer.Model.Name);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns a set of printer ID strings that already have at least one unread
    /// CatalogUpdateAvailable notification, to avoid duplicate notification spam.
    /// </summary>
    private static async Task<HashSet<string>> GetAlreadyNotifiedPrinterIdsAsync(
        AppDbContext db,
        CancellationToken ct)
    {
        List<string?> existingMetadata = await db.Notifications
            .AsNoTracking()
            .Where(n => n.Type == NotificationType.CatalogUpdateAvailable && !n.IsRead)
            .Select(n => n.Metadata)
            .Distinct()
            .ToListAsync(ct);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? meta in existingMetadata)
        {
            if (string.IsNullOrEmpty(meta))
            {
                continue;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(meta);
                if (doc.RootElement.TryGetProperty("printerId", out JsonElement el))
                {
                    string? id = el.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        result.Add(id);
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed metadata entries
            }
        }

        return result;
    }
}
