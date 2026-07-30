using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Farm.Infrastructure.Dtos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Admin;

/// <summary>
/// Aggregates <see cref="HealthCheckService"/> results into a single admin overview snapshot.
/// This does not run any new probes — it composes the health checks already registered by
/// <c>AddPrintFarmerHealthChecks()</c>: <c>comprehensive</c>, <c>signalr</c>, and <c>spoolman</c>.
///
/// The comprehensive check bundles several sub-probes into its <see cref="HealthReportEntry.Data"/>
/// dictionary (Database, CatalogApi, FilamentTypesApi, ExternalServices, Memory, Application).
/// This service reads those payloads to split the single check into the finer per-subsystem tiles
/// the Control Center UI needs (database, api, backends).
/// </summary>
public sealed class AdminOverviewService : IAdminOverviewService
{
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(8);

    private readonly HealthCheckService _healthCheckService;
    private readonly ILogger<AdminOverviewService> _logger;

    public AdminOverviewService(HealthCheckService healthCheckService, ILogger<AdminOverviewService> logger)
    {
        _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AdminOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        DateTime checkedAt = DateTime.UtcNow;

        HealthReport? report = null;
        string? probeError = null;

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(HealthCheckTimeout);
            report = await _healthCheckService.CheckHealthAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled — propagate so ASP.NET can short-circuit properly.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Admin overview: health check aggregation timed out after {Timeout}", HealthCheckTimeout);
            probeError = $"Health checks did not complete within {HealthCheckTimeout.TotalSeconds:0} seconds.";
        }
#pragma warning disable CA1031 // Aggregation must never fail the endpoint; log and mark subsystems Unknown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Admin overview: health check aggregation threw");
            probeError = "Health check aggregation failed: " + ex.Message;
        }

        List<SubsystemHealthDto> subsystems = BuildSubsystems(report, probeError);
        List<AttentionItemDto> attention = BuildAttention(report, probeError);

        return new AdminOverviewDto
        {
            CheckedAt = checkedAt,
            Subsystems = subsystems,
            Attention = attention,
        };
    }

    private static List<SubsystemHealthDto> BuildSubsystems(HealthReport? report, string? probeError)
    {
        // The API is answering this very request, so it is by definition responding.
        // Its status downgrades only when the aggregation itself failed (in which case
        // we cannot claim confidence in anything else either).
        List<SubsystemHealthDto> tiles = new()
        {
            new SubsystemHealthDto
            {
                Key = "api",
                Name = "API",
                Status = probeError is null ? SubsystemStatus.Healthy : SubsystemStatus.Degraded,
                Detail = probeError is null ? "Responding" : "Health probes did not report cleanly",
            },
        };

        if (report is null)
        {
            // Total probe failure: emit the remaining core subsystems as Unknown so the
            // UI can render tiles rather than showing nothing.
            tiles.Add(UnknownTile("database", "Database", probeError));
            tiles.Add(UnknownTile("signalr", "SignalR Hub", probeError));
            tiles.Add(UnknownTile("backends", "Printer Backends", probeError));
            return tiles;
        }

        report.Entries.TryGetValue("comprehensive", out HealthReportEntry comprehensive);
        report.Entries.TryGetValue("signalr", out HealthReportEntry signalr);
        report.Entries.TryGetValue("spoolman", out HealthReportEntry spoolman);

        // Database — pulled from comprehensive.Data["Database"] when available; falls back
        // to the top-level comprehensive status if the sub-payload is unreadable.
        tiles.Add(BuildTileFromSubcheck(
            key: "database",
            name: "Database",
            fallbackEntry: comprehensive,
            data: comprehensive.Data,
            subCheckKey: "Database",
            detailBuilder: DatabaseDetail));

        // SignalR is its own registered check, so map its status directly.
        tiles.Add(BuildTileFromEntry("signalr", "SignalR Hub", signalr, SignalRDetail));

        // Backends — pulled from comprehensive.Data["ExternalServices"] (printer HTTP probes).
        tiles.Add(BuildTileFromSubcheck(
            key: "backends",
            name: "Printer Backends",
            fallbackEntry: comprehensive,
            data: comprehensive.Data,
            subCheckKey: "ExternalServices",
            detailBuilder: BackendsDetail));

        // Spoolman is optional; hide the tile when it explicitly reports "not configured".
        if (IsSpoolmanConfigured(spoolman))
        {
            tiles.Add(BuildTileFromEntry("spoolman", "Spoolman", spoolman, SpoolmanDetail));
        }

        return tiles;
    }

    private static SubsystemHealthDto UnknownTile(string key, string name, string? detail) => new()
    {
        Key = key,
        Name = name,
        Status = SubsystemStatus.Unknown,
        Detail = detail,
    };

    private static SubsystemHealthDto BuildTileFromEntry(
        string key,
        string name,
        HealthReportEntry entry,
        Func<HealthReportEntry, string?> detailBuilder)
    {
        return new SubsystemHealthDto
        {
            Key = key,
            Name = name,
            Status = MapStatus(entry.Status),
            Detail = detailBuilder(entry),
        };
    }

    private static SubsystemHealthDto BuildTileFromSubcheck(
        string key,
        string name,
        HealthReportEntry fallbackEntry,
        IReadOnlyDictionary<string, object>? data,
        string subCheckKey,
        Func<IDictionary<string, object?>?, HealthReportEntry, string?> detailBuilder)
    {
        Dictionary<string, object?>? subData = null;
        SubsystemStatus? subStatus = null;

        if (data is not null && data.TryGetValue(subCheckKey, out object? subObj) && subObj is not null)
        {
            subData = ReadAnonymous(subObj);
            if (subData is not null && subData.TryGetValue("Status", out object? statusVal) && statusVal is string statusStr)
            {
                subStatus = ParseStatusString(statusStr);
            }
        }

        SubsystemStatus status = subStatus ?? MapStatus(fallbackEntry.Status);
        string? detail = detailBuilder(subData, fallbackEntry);

        return new SubsystemHealthDto
        {
            Key = key,
            Name = name,
            Status = status,
            Detail = detail,
        };
    }

    private static string? DatabaseDetail(IDictionary<string, object?>? subData, HealthReportEntry fallbackEntry)
    {
        if (subData is null)
        {
            return TruncateDescription(fallbackEntry.Description);
        }

        string? provider = TryReadString(subData, "Provider");
        int? count = TryReadInt(subData, "ManufacturerCount");
        bool? initialized = TryReadBool(subData, "Initialized");
        string? error = TryReadString(subData, "Error");

        if (!string.IsNullOrWhiteSpace(error))
        {
            return "Error: " + error;
        }

        string providerShort = string.IsNullOrEmpty(provider) ? "unknown provider" : ShortProvider(provider);
        if (initialized == false)
        {
            return $"{providerShort} · not initialized";
        }

        if (count.HasValue)
        {
            return $"{providerShort} · seeded ({count.Value} manufacturers)";
        }

        return providerShort;
    }

    private static string? SignalRDetail(HealthReportEntry entry)
    {
        return entry.Status == HealthStatus.Healthy
            ? "Hub accessible"
            : TruncateDescription(entry.Description) ?? "See health details";
    }

    private static string? BackendsDetail(IDictionary<string, object?>? subData, HealthReportEntry fallbackEntry)
    {
        if (subData is null)
        {
            return TruncateDescription(fallbackEntry.Description);
        }

        int? checkedCount = TryReadInt(subData, "CheckedCount");
        int? failedCount = TryReadInt(subData, "FailedCount");
        string? error = TryReadString(subData, "Error");

        if (!string.IsNullOrWhiteSpace(error))
        {
            return "Error: " + error;
        }

        if (checkedCount is null)
        {
            return null;
        }

        if (checkedCount == 0)
        {
            return "No external printers probed";
        }

        int reachable = checkedCount.Value - (failedCount ?? 0);
        return string.Create(CultureInfo.InvariantCulture, $"{reachable} / {checkedCount.Value} reachable");
    }

    private static string? SpoolmanDetail(HealthReportEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            return TruncateDescription(entry.Description);
        }

        return entry.Status switch
        {
            HealthStatus.Healthy => "Reachable",
            HealthStatus.Degraded => "Unreachable",
            HealthStatus.Unhealthy => "Failing",
            _ => null,
        };
    }

    private static bool IsSpoolmanConfigured(HealthReportEntry entry)
    {
        // Spoolman returns Healthy with description "Spoolman not configured" when
        // no BaseUrl is set. Hide the tile in that case — nothing to show.
        if (entry.Status != HealthStatus.Healthy)
        {
            return true;
        }

        return entry.Description is not null
            && !entry.Description.Contains("not configured", StringComparison.OrdinalIgnoreCase);
    }

    // Stable ids from ADMIN_DESTINATIONS (src/Web/ReactApp/src/features/admin/registry/adminDestinations.ts).
    // The client resolves these to canonical paths so route renames stay a frontend concern.
    // If either id is renamed or removed, the corresponding attention item link will fail to render —
    // this is intentional and caught by the frontend registry tests.
    private const string OpsStatusDestinationId = "ops-status";

    private static List<AttentionItemDto> BuildAttention(HealthReport? report, string? probeError)
    {
        List<AttentionItemDto> items = new();

        if (probeError is not null)
        {
            items.Add(new AttentionItemDto
            {
                Key = "admin-overview-probe-failed",
                Severity = AttentionSeverity.Error,
                Title = "System health probes are not reporting",
                Detail = probeError,
                ActionLabel = "Open System logs",
                ActionDestinationId = OpsStatusDestinationId,
            });
        }

        if (report is not null)
        {
            foreach ((string entryName, HealthReportEntry entry) in report.Entries)
            {
                AppendAttentionForEntry(entryName, entry, items);
            }
        }

        // Sort: Error > Warning > Info, then stable by Title.
        items.Sort((a, b) =>
        {
            int severity = ((int)b.Severity).CompareTo((int)a.Severity);
            return severity != 0
                ? severity
                : string.CompareOrdinal(a.Title, b.Title);
        });

        return items;
    }

    private static void AppendAttentionForEntry(string entryName, HealthReportEntry entry, List<AttentionItemDto> items)
    {
        // Extract per-printer failures from ExternalServices, if the payload exposes them.
        if (string.Equals(entryName, "comprehensive", StringComparison.Ordinal))
        {
            AppendDatabaseAttention(entry, items);
            AppendExternalServicesAttention(entry, items);
        }
        else if (entry.Status != HealthStatus.Healthy)
        {
            items.Add(new AttentionItemDto
            {
                Key = $"health-{entryName}",
                Severity = entry.Status == HealthStatus.Unhealthy ? AttentionSeverity.Error : AttentionSeverity.Warning,
                Title = FriendlyEntryTitle(entryName),
                Detail = string.IsNullOrWhiteSpace(entry.Description)
                    ? "Health check reported " + entry.Status.ToString().ToLowerInvariant() + " status."
                    : entry.Description,
                ActionLabel = "Open System logs",
                ActionDestinationId = OpsStatusDestinationId,
            });
        }
    }

    private static void AppendDatabaseAttention(HealthReportEntry comprehensive, List<AttentionItemDto> items)
    {
        if (comprehensive.Data is null || !comprehensive.Data.TryGetValue("Database", out object? dbObj) || dbObj is null)
        {
            return;
        }

        Dictionary<string, object?>? data = ReadAnonymous(dbObj);
        if (data is null)
        {
            return;
        }

        string? statusStr = TryReadString(data, "Status");
        if (statusStr is null || string.Equals(statusStr, "Healthy", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? error = TryReadString(data, "Error");
        bool? initialized = TryReadBool(data, "Initialized");

        string detail = error ?? (initialized == false
            ? "Database is reachable but has no seed data — the app may not function correctly."
            : "Database status: " + statusStr);

        items.Add(new AttentionItemDto
        {
            Key = "database-" + statusStr.ToLowerInvariant(),
            Severity = AttentionSeverity.Error,
            Title = "Database is not healthy",
            Detail = detail,
            ActionLabel = "Open System info",
            ActionDestinationId = OpsStatusDestinationId,
        });
    }

    private static void AppendExternalServicesAttention(HealthReportEntry comprehensive, List<AttentionItemDto> items)
    {
        if (comprehensive.Data is null
            || !comprehensive.Data.TryGetValue("ExternalServices", out object? extObj)
            || extObj is null)
        {
            return;
        }

        Dictionary<string, object?>? data = ReadAnonymous(extObj);
        if (data is null)
        {
            return;
        }

        int failedCount = TryReadInt(data, "FailedCount") ?? 0;
        if (failedCount == 0)
        {
            return;
        }

        if (!data.TryGetValue("FailedServicesDetails", out object? detailsObj) || detailsObj is not IEnumerable enumerable)
        {
            int checkedCount = TryReadInt(data, "CheckedCount") ?? 0;
            items.Add(new AttentionItemDto
            {
                Key = "backends-degraded",
                Severity = AttentionSeverity.Warning,
                Title = "Some printer backends are unreachable",
                Detail = string.Create(CultureInfo.InvariantCulture, $"{failedCount} of {checkedCount} probed printer backends failed to respond."),
                ActionLabel = "Open Printers",
                ActionRoute = "/printers",
            });
            return;
        }

        int reported = 0;
        foreach (object? entry in enumerable)
        {
            if (entry is null)
            {
                continue;
            }

            Dictionary<string, object?>? printerData = ReadAnonymous(entry);
            if (printerData is null)
            {
                continue;
            }

            string? printerId = TryReadString(printerData, "Id");
            string name = TryReadString(printerData, "Name") ?? "Unknown printer";
            string? errorMessage = TryReadString(printerData, "ErrorMessage");
            int? statusCode = TryReadInt(printerData, "StatusCode");
            string? url = TryReadString(printerData, "AttemptedUrl") ?? TryReadString(printerData, "ServerUrl");

            string reasonPart;
            if (statusCode.HasValue)
            {
                reasonPart = string.Create(CultureInfo.InvariantCulture, $"HTTP {statusCode.Value}");
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                reasonPart = errorMessage;
            }
            else
            {
                reasonPart = "no response";
            }

            string detail = url is null
                ? $"{name} did not respond ({reasonPart})."
                : $"{name} did not respond at {url} ({reasonPart}).";

            items.Add(new AttentionItemDto
            {
                Key = printerId is null ? $"printer-unreachable-{reported}" : "printer-" + printerId + "-unreachable",
                Severity = AttentionSeverity.Warning,
                Title = $"Printer '{name}' is unreachable",
                Detail = detail,
                ActionLabel = "Open Printers",
                ActionRoute = "/printers",
            });
            reported++;
        }
    }

    private static SubsystemStatus MapStatus(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => SubsystemStatus.Healthy,
        HealthStatus.Degraded => SubsystemStatus.Degraded,
        HealthStatus.Unhealthy => SubsystemStatus.Unhealthy,
        _ => SubsystemStatus.Unknown,
    };

    private static SubsystemStatus ParseStatusString(string status) => status switch
    {
        "Healthy" => SubsystemStatus.Healthy,
        "Degraded" => SubsystemStatus.Degraded,
        "Unhealthy" => SubsystemStatus.Unhealthy,
        "Warning" => SubsystemStatus.Degraded,
        "Error" => SubsystemStatus.Unhealthy,
        _ => SubsystemStatus.Unknown,
    };

    private static string FriendlyEntryTitle(string entryName) => entryName switch
    {
        "signalr" => "SignalR hub is not healthy",
        "spoolman" => "Spoolman integration is not healthy",
        "comprehensive" => "Core systems are not healthy",
        _ => $"Health check '{entryName}' is not healthy",
    };

    private static string ShortProvider(string provider)
    {
        // "Microsoft.EntityFrameworkCore.Sqlite" → "SQLite"
        int lastDot = provider.LastIndexOf('.');
        return lastDot >= 0 && lastDot < provider.Length - 1 ? provider[(lastDot + 1)..] : provider;
    }

    private static string? TruncateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        const int Max = 140;
        return description.Length <= Max ? description : description[..(Max - 1)] + "…";
    }

    /// <summary>
    /// Reads a heterogeneous object into a case-insensitive dictionary. Handles both
    /// concrete <see cref="IDictionary{TKey,TValue}"/> instances (as used by ExternalServices)
    /// and anonymous types (as used by the other sub-checks) via reflection. Never throws.
    /// </summary>
    [DebuggerStepThrough]
    private static Dictionary<string, object?>? ReadAnonymous(object obj)
    {
        try
        {
            if (obj is IDictionary<string, object> raw)
            {
                return new Dictionary<string, object?>(
                    raw.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (obj is System.Collections.IDictionary weakDict)
            {
                Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in weakDict)
                {
                    if (entry.Key is string key)
                    {
                        result[key] = entry.Value;
                    }
                }

                return result;
            }

            // Anonymous/record types: read public instance properties.
            System.Reflection.PropertyInfo[] props = obj.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (props.Length == 0)
            {
                return null;
            }

            Dictionary<string, object?> map = new(StringComparer.OrdinalIgnoreCase);
            foreach (System.Reflection.PropertyInfo prop in props)
            {
                if (prop.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    map[prop.Name] = prop.GetValue(obj);
                }
#pragma warning disable CA1031 // Best-effort reflection reader — skip any property whose getter throws.
                catch
#pragma warning restore CA1031
                {
                }
            }

            return map;
        }
#pragma warning disable CA1031 // Reading heterogeneous payloads must never fail the aggregation.
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static string? TryReadString(IDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            _ => value.ToString(),
        };
    }

    private static int? TryReadInt(IDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
            double d => (int)Math.Round(d),
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };
    }

    private static bool? TryReadBool(IDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out bool parsed) => parsed,
            _ => null,
        };
    }
}
