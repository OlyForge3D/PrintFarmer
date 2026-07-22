using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure.Services.ShiftPlan.Sources;

/// <summary>
/// Typed evidence retained with a source-qualified spool-restock task.
/// </summary>
public sealed record SpoolRestockTaskMetadata(
    SpoolSourceKind SourceKind,
    string SourceIdentity,
    int SpoolId,
    string SpoolName,
    double? RemainingGrams,
    double AuthoritativeGramsConsumed,
    double? BurnRateGramsPerDay,
    double ThresholdGrams,
    DateTime ProjectedThresholdCrossingUtc,
    DateTime ActionAtUtc,
    DateTime EvaluatedAtUtc,
    int SampleCount,
    SpoolBurnRateProjectionState State);

/// <summary>
/// Projects current source-qualified spool occurrences into restock shift-plan tasks.
/// </summary>
public sealed class SpoolRestockShiftPlanTaskSource(
    AppDbContext db,
    ISpoolBurnRateProjectionService projectionService,
    IFilamentCoverageSpoolResolver spoolResolver,
    ISpoolmanService spoolmanService,
    ISettingsService settingsService,
    IOperatorFeatureGate featureGate,
    ILogger<SpoolRestockShiftPlanTaskSource>? logger = null,
    IMutationWatermarkReader? watermarkReader = null,
    TimeProvider? clock = null) : IShiftPlanTaskSource
{
    private const string SourceIdPrefix = "spoolrestock:v1:";
    private const int TitleMaxLength = 200;
    private const int DescriptionMaxLength = 1000;
    private const string TitlePrefix = "Restock ";
    private const double RemainingWeightComparisonTolerance = 1e-9;

    private static readonly JsonSerializerOptions MetadataJsonOptions = CreateMetadataJsonOptions();

    private readonly ILogger<SpoolRestockShiftPlanTaskSource> _logger =
        logger ?? NullLogger<SpoolRestockShiftPlanTaskSource>.Instance;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public string SourceName => "spool-restock";

    /// <inheritdoc />
    public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
        [UserTaskSourceKind.SpoolReorder];

    /// <inheritdoc />
    public async Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct)
    {
        long? originWatermark = await OriginWatermark
            .CaptureAsync(
                watermarkReader,
                _logger,
                "spool-restock source",
                ct)
            .ConfigureAwait(false);

        await EnsureFeatureEnabledAsync(ct).ConfigureAwait(false);
        RestockSettingsSnapshot initialSettings = ReadSettings();
        string? initialCentralSource = spoolmanService.GetConfig()?.BaseUrl;
        HashSet<CanonicalSpoolIdentity> initialOccurrences = await LoadOccurrencesAsync(
            initialCentralSource,
            ct).ConfigureAwait(false);

        List<ShiftPlanTaskSpec> specs = new(initialOccurrences.Count);
        HashSet<string> preservedSourceIds = new(StringComparer.Ordinal);
        foreach (CanonicalSpoolIdentity identity in OrderOccurrences(initialOccurrences))
        {
            ct.ThrowIfCancellationRequested();
            string sourceId = BuildSourceId(identity);
            DateTime callStartedAtUtc = _clock.GetUtcNow().UtcDateTime;
            SpoolBurnRateProjectionDto projection = await projectionService
                .ProjectAsync(identity, ct)
                .ConfigureAwait(false);
            DateTime callCompletedAtUtc = _clock.GetUtcNow().UtcDateTime;

            EnsureProjectionIdentity(identity, projection);
            if (IsLocallyStale(projection, callStartedAtUtc, callCompletedAtUtc))
            {
                _ = preservedSourceIds.Add(sourceId);
                continue;
            }

            switch (projection.State)
            {
                case SpoolBurnRateProjectionState.Ready:
                    FilamentCoverageSpoolSnapshot spoolSnapshot = await spoolResolver
                        .ResolveSpoolAsync(identity, ct)
                        .ConfigureAwait(false);
                    specs.Add(ToSpec(
                        identity,
                        projection,
                        RequireMatchingSpool(identity, projection, spoolSnapshot),
                        initialSettings));
                    break;

                case SpoolBurnRateProjectionState.InsufficientData:
                case SpoolBurnRateProjectionState.SourceUnavailable:
                    if (projection.ProjectedThresholdCrossingUtc.HasValue)
                    {
                        throw new InvalidOperationException(
                            $"Non-ready projection for {sourceId} unexpectedly included a threshold crossing.");
                    }

                    _ = preservedSourceIds.Add(sourceId);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Projection for {sourceId} returned unknown state {projection.State}.");
            }
        }

        await EnsureFeatureEnabledAsync(ct).ConfigureAwait(false);
        RestockSettingsSnapshot finalSettings = ReadSettings();
        if (initialSettings != finalSettings)
        {
            throw new InvalidOperationException(
                "Spool-restock evaluation is incomplete because relevant settings changed during observation.");
        }

        string? finalCentralSource = spoolmanService.GetConfig()?.BaseUrl;
        if (!string.Equals(initialCentralSource, finalCentralSource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Spool-restock evaluation is incomplete because central Spoolman configuration changed during observation.");
        }

        HashSet<CanonicalSpoolIdentity> finalOccurrences = await LoadOccurrencesAsync(
            finalCentralSource,
            ct).ConfigureAwait(false);
        if (!initialOccurrences.SetEquals(finalOccurrences))
        {
            throw new InvalidOperationException(
                "Spool-restock evaluation is incomplete because spool assignments changed during observation.");
        }

        List<ShiftPlanTaskSpec> orderedSpecs = specs
            .OrderBy(spec => spec.SourceId, StringComparer.Ordinal)
            .ToList();
        return new ShiftPlanSourceResult(orderedSpecs, originWatermark)
        {
            Authority = new ShiftPlanSourceAuthority(
            [
                new ShiftPlanKindAuthority(
                    UserTaskSourceKind.SpoolReorder,
                    IsAuthoritativeComplete: true,
                    PreservedSourceIds: preservedSourceIds,
                    IncompleteReasons: []),
            ]),
        };
    }

    private async Task EnsureFeatureEnabledAsync(CancellationToken ct)
    {
        bool enabled = await featureGate
            .IsEnabledStrictAsync(OperatorFeature.ShiftPlan, ct)
            .ConfigureAwait(false);
        if (!enabled)
        {
            throw new InvalidOperationException(
                "Spool-restock evaluation is incomplete because the shift-plan feature is disabled.");
        }
    }

    private RestockSettingsSnapshot ReadSettings()
    {
        ShiftPlanSettings settings = settingsService.Get<ShiftPlanSettings>();
        settings.Validate();
        return new RestockSettingsSnapshot(
            settings.SpoolReorderThresholdGrams,
            settings.SpoolRestockLeadMinutes,
            settings.SpoolBurnRateLookbackDays,
            settings.SpoolBurnRateMinimumSamples);
    }

    private async Task<HashSet<CanonicalSpoolIdentity>> LoadOccurrencesAsync(
        string? centralSource,
        CancellationToken ct)
    {
        List<Printer> printers = await db.Printers
            .AsNoTracking()
            .Include(printer => printer.Toolheads)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        HashSet<CanonicalSpoolIdentity> occurrences = [];
        foreach (Printer printer in printers)
        {
            HashSet<int> spoolIds = [];
            List<Toolhead> filamentSources = printer.Toolheads
                .Where(toolhead =>
                    ToolheadIndexMapper.IsFilamentSource(toolhead, printer.Toolheads))
                .ToList();
            foreach (Toolhead toolhead in filamentSources)
            {
                int? effectiveSpoolId =
                    toolhead.CurrentSpoolId
                    ?? (toolhead.IsPrimary ? printer.CurrentSpoolId : null);
                if (effectiveSpoolId is int toolheadSpoolId)
                {
                    AddQualifiedOccurrence(printer, toolheadSpoolId, centralSource, spoolIds, occurrences);
                }
            }

            if (filamentSources.Count == 0
                && printer.CurrentSpoolId is int legacySpoolId)
            {
                AddQualifiedOccurrence(printer, legacySpoolId, centralSource, spoolIds, occurrences);
            }
        }

        return occurrences;
    }

    private static void AddQualifiedOccurrence(
        Printer printer,
        int spoolId,
        string? centralSource,
        HashSet<int> printerSpoolIds,
        HashSet<CanonicalSpoolIdentity> occurrences)
    {
        if (!printerSpoolIds.Add(spoolId))
        {
            return;
        }

        CanonicalSpoolIdentity? identity =
            CanonicalSpoolIdentity.FromPrinter(printer, spoolId, centralSource);
        if (!identity.HasValue)
        {
            throw new InvalidOperationException(
                $"Printer {printer.Id:D} has spool assignment {spoolId} that cannot be source-qualified.");
        }

        _ = occurrences.Add(identity.Value);
    }

    private ShiftPlanTaskSpec ToSpec(
        CanonicalSpoolIdentity identity,
        SpoolBurnRateProjectionDto projection,
        SpoolmanSpoolDto spool,
        RestockSettingsSnapshot settings)
    {
        ValidateReadyProjection(identity, projection);
        DateTime thresholdCrossingUtc = projection.ProjectedThresholdCrossingUtc!.Value;
        DateTime actionAtUtc = thresholdCrossingUtc.AddMinutes(-settings.LeadMinutes);
        DateTime nowUtc = _clock.GetUtcNow().UtcDateTime;
        string spoolName = EnsureValidUtf16(spool.Name);

        SpoolRestockTaskMetadata metadata = new(
            identity.SourceKind,
            identity.SourceIdentity,
            identity.SpoolId,
            spoolName,
            projection.RemainingGrams,
            projection.AuthoritativeGramsConsumed,
            projection.BurnRateGramsPerDay,
            settings.ThresholdGrams,
            thresholdCrossingUtc,
            actionAtUtc,
            projection.EvaluatedAtUtc,
            projection.SampleCount,
            projection.State);

        (string sourceId, Guid entityId) = BuildTaskIdentity(identity);
        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"{spoolName}: {projection.RemainingGrams:0.##} g remaining at {projection.BurnRateGramsPerDay:0.##} g/day; projected to cross {settings.ThresholdGrams:0.##} g at {thresholdCrossingUtc:O}.");

        bool isFuture = actionAtUtc > nowUtc;
        return new ShiftPlanTaskSpec(
            TaskType: UserTaskType.SpoolRestock,
            SourceKind: UserTaskSourceKind.SpoolReorder,
            SourceId: sourceId,
            Title: BoundText($"{TitlePrefix}{spoolName}", TitleMaxLength),
            Description: BoundText(description, DescriptionMaxLength),
            Priority: UserTaskPriority.Normal,
            AnchorKind: isFuture ? UserTaskAnchorKind.At : UserTaskAnchorKind.Now,
            AnchorAtUtc: isFuture ? actionAtUtc : null,
            WindowStartUtc: null,
            WindowEndUtc: null,
            EntityType: "Spool",
            EntityId: entityId,
            DueAt: thresholdCrossingUtc,
            MetadataJson: JsonSerializer.Serialize(metadata, MetadataJsonOptions));
    }

    private static SpoolmanSpoolDto RequireMatchingSpool(
        CanonicalSpoolIdentity identity,
        SpoolBurnRateProjectionDto projection,
        FilamentCoverageSpoolSnapshot snapshot)
    {
        if (snapshot.ErrorReason is not null
            || snapshot.Spool is not SpoolmanSpoolDto spool
            || spool.Id != identity.SpoolId
            || !SameRemainingWeight(spool.RemainingWeightG, projection.RemainingGrams))
        {
            throw new InvalidOperationException(
                $"Ready spool {BuildSourceId(identity)} could not be resolved from its canonical source.");
        }

        return spool;
    }

    private static bool SameRemainingWeight(double? current, double? projected) =>
        current is double currentValue
        && projected is double projectedValue
        && double.IsFinite(currentValue)
        && double.IsFinite(projectedValue)
        && Math.Abs(currentValue - projectedValue) <= RemainingWeightComparisonTolerance;

    private static void EnsureProjectionIdentity(
        CanonicalSpoolIdentity expected,
        SpoolBurnRateProjectionDto projection)
    {
        if (projection.SourceKind != expected.SourceKind
            || projection.SpoolId != expected.SpoolId
            || !string.Equals(
                projection.SourceIdentity,
                expected.SourceIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projection identity did not match requested spool {BuildSourceId(expected)}.");
        }
    }

    private static bool IsLocallyStale(
        SpoolBurnRateProjectionDto projection,
        DateTime callStartedAtUtc,
        DateTime callCompletedAtUtc) =>
        projection.EvaluatedAtUtc.Kind != DateTimeKind.Utc
        || projection.EvaluatedAtUtc < callStartedAtUtc
        || projection.EvaluatedAtUtc > callCompletedAtUtc;

    private static void ValidateReadyProjection(
        CanonicalSpoolIdentity identity,
        SpoolBurnRateProjectionDto projection)
    {
        if (projection.ProjectedThresholdCrossingUtc is not DateTime crossing
            || crossing.Kind != DateTimeKind.Utc
            || projection.RemainingGrams is not double remaining
            || remaining < 0
            || !double.IsFinite(remaining)
            || projection.BurnRateGramsPerDay is not double rate
            || rate <= 0
            || !double.IsFinite(rate)
            || projection.AuthoritativeGramsConsumed < 0
            || !double.IsFinite(projection.AuthoritativeGramsConsumed)
            || projection.SampleCount < 0)
        {
            throw new InvalidOperationException(
                $"Ready projection for {BuildSourceId(identity)} contained invalid evidence.");
        }
    }

    private static IEnumerable<CanonicalSpoolIdentity> OrderOccurrences(
        IEnumerable<CanonicalSpoolIdentity> occurrences) =>
        occurrences
            .OrderBy(identity => identity.SourceKind)
            .ThenBy(identity => identity.SourceIdentity, StringComparer.Ordinal)
            .ThenBy(identity => identity.SpoolId);

    private static string BuildSourceId(CanonicalSpoolIdentity identity) =>
        BuildTaskIdentity(identity).SourceId;

    private static (string SourceId, Guid EntityId) BuildTaskIdentity(
        CanonicalSpoolIdentity identity)
    {
        string canonicalKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)identity.SourceKind}\u001f{identity.SourceIdentity}\u001f{identity.SpoolId}");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey));
        string digestHex = Convert.ToHexStringLower(digest);
        return (
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SourceIdPrefix}{identity.SpoolId}:{digestHex}"),
            new Guid(digest.AsSpan(0, 16)));
    }

    private static string EnsureValidUtf16(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            _ = builder.Append(rune);
        }

        return builder.ToString();
    }

    private static string BoundText(string value, int maximumLength)
    {
        string validValue = EnsureValidUtf16(value);
        if (validValue.Length <= maximumLength)
        {
            return validValue;
        }

        StringBuilder builder = new(maximumLength);
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(validValue);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (builder.Length + element.Length > maximumLength)
            {
                break;
            }

            _ = builder.Append(element);
        }

        return builder.ToString();
    }

    private static JsonSerializerOptions CreateMetadataJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record RestockSettingsSnapshot(
        double ThresholdGrams,
        int LeadMinutes,
        int LookbackDays,
        int MinimumSamples);
}
