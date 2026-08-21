using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.HostedServices;

/// <summary>
/// Backfills <see cref="Model3D.DimensionX"/>/<see cref="Model3D.DimensionY"/>/
/// <see cref="Model3D.DimensionZ"/>/<see cref="Model3D.TriangleCount"/> for rows created before real
/// geometry analysis existed (#1814). Every model in the library reported these fields as null and
/// <see cref="Model3D.IsValid"/> as an unconditional <c>true</c>, because upload-time analysis
/// either wasn't wired up (standalone slicer-host) or only supported STL with a case-sensitive
/// extension check. This service re-runs analysis for existing rows so the library reflects real
/// geometry data without requiring a re-upload.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a background reconciliation pass, not a schema migration: no column shape
/// changed, only stored values for existing rows, so it runs once per start against whatever rows
/// still need it and is a no-op once the library is fully backfilled.
/// </para>
/// <para>
/// <b>Failure semantics.</b> Mirrors <see cref="SystemProfileReconciliationService"/>: it never
/// blocks host startup (first action is a delay), and every failure is caught and logged rather than
/// rethrown, since .NET's default <c>BackgroundServiceExceptionBehavior</c> is <c>StopHost</c> — an
/// error here must never turn into an outage over cosmetic metadata.
/// </para>
/// <para>
/// <b>Concurrency.</b> Unlike <see cref="SystemProfileReconciliationService"/>'s reconciliation,
/// which relies on database unique indexes to reject a losing replica's duplicate insert, this
/// service has no equivalent structural guard, and none is added here (#1837). If the host runs
/// more than one replica, each pulls its own batch from
/// <see cref="IModel3DFileRepository.ListNeedingAnalysisAsync"/> independently, so overlapping
/// batches are possible between replicas that start their pass around the same time (a row can be
/// selected by more than one replica before either has written its result). This is deliberately
/// accepted rather than mitigated with a distributed lock or leader election, because the outcome
/// is benign: analysis is a pure, deterministic function of file bytes, so redoing it produces the
/// same result, and this method updates a row's own columns by primary key rather than inserting
/// against a unique constraint, so there is nothing for concurrent writers to collide on — the
/// worst case is last-write-wins with identical values, plus wasted CPU re-analyzing a file more
/// than once. That waste is bounded by <c>BatchSize</c> per pass and disappears once the library is
/// fully backfilled (the loop then finds nothing left to do), so it was judged not worth the added
/// complexity of a distributed lock for what is a one-time reconciliation pass, not a steady-state
/// workload. A future change that makes this ongoing (e.g. re-analyzing on every file change) should
/// revisit this assumption.
/// </para>
/// <para>
/// <b>Per-row resilience.</b> A single unreadable file (missing from disk, corrupt archive, unknown
/// format) must not abort the whole batch, and must not be retried forever on every future start
/// either. So a row that cannot be analyzed is still updated — with <c>TriangleCount = 0</c>,
/// <see cref="Model3D.IsValid"/> set to <c>false</c>, and a explanatory
/// <see cref="Model3D.ValidationErrors"/> entry — which removes it from the
/// "still needs analysis" query (<see cref="IModel3DFileRepository.ListNeedingAnalysisAsync"/>
/// filters on <c>TriangleCount == null</c>) while still recording that geometry could not be
/// confirmed, consistent with the narrow definition of <see cref="Model3D.IsValid"/> used at upload
/// time (structural readability, never a printability/orientation judgment — see #1811).
/// </para>
/// </remarks>
public sealed class ModelMetadataBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ModelMetadataBackfillService> _logger;
    private readonly bool _enabled;
    private readonly TimeSpan _startupDelay;
    private readonly int _batchSize;

    public ModelMetadataBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<ModelMetadataBackfillService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArgumentNullException.ThrowIfNull(configuration);
        _enabled = configuration.GetValue("ModelMetadataBackfill:Enabled", true);
        _startupDelay = TimeSpan.FromSeconds(configuration.GetValue("ModelMetadataBackfill:StartupDelaySeconds", 30));
        _batchSize = configuration.GetValue("ModelMetadataBackfill:BatchSize", 50);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("[ModelMetadataBackfill] Disabled via configuration (ModelMetadataBackfill:Enabled=false)");
            return;
        }

        try
        {
            // Let the app finish starting before adding load.
            await Task.Delay(_startupDelay, stoppingToken);
            await BackfillAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ModelMetadataBackfill] Backfill pass failed; remaining rows will be retried on the next start");
        }
    }

    internal async Task BackfillAsync(CancellationToken ct)
    {
        int totalUpdated = 0;
        int totalFailed = 0;

        while (!ct.IsCancellationRequested)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IModel3DFileRepository repository = scope.ServiceProvider.GetRequiredService<IModel3DFileRepository>();
            IModelAnalysisService? analysisService = scope.ServiceProvider.GetService<IModelAnalysisService>();
            if (analysisService is null)
            {
                _logger.LogInformation("[ModelMetadataBackfill] Model analysis service not registered; skipping");
                return;
            }

            IStoragePathService storagePathService = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
            string modelsPath = storagePathService.GetModelUploadDirectory();

            List<Model3D> batch = await repository.ListNeedingAnalysisAsync(_batchSize, ct);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (Model3D model in batch)
            {
                ct.ThrowIfCancellationRequested();
                await AnalyzeAndUpdateAsync(model, analysisService, modelsPath, ct);
                if (model.TriangleCount is > 0)
                {
                    totalUpdated++;
                }
                else
                {
                    totalFailed++;
                }
            }

            await repository.SaveChangesAsync(ct);

            if (batch.Count < _batchSize)
            {
                break;
            }
        }

        if (totalUpdated > 0 || totalFailed > 0)
        {
            _logger.LogInformation(
                "[ModelMetadataBackfill] Backfilled geometry metadata for {Updated} model(s); {Failed} could not be analyzed",
                totalUpdated,
                totalFailed);
        }
        else
        {
            _logger.LogInformation("[ModelMetadataBackfill] No models needed geometry metadata backfill");
        }
    }

    private async Task AnalyzeAndUpdateAsync(
        Model3D model,
        IModelAnalysisService analysisService,
        string modelsPath,
        CancellationToken ct)
    {
        string filePath = Path.Join(modelsPath, model.FileName);
        string extension = Path.GetExtension(model.FileName);

        try
        {
            if (!File.Exists(filePath))
            {
                MarkUnanalyzable(model, "Model file was not found on disk during metadata backfill");
                return;
            }

            ModelAnalysisResult? analysis = await analysisService.AnalyzeModelAsync(filePath, extension, ct);
            if (analysis is null)
            {
                // Unsupported format reaching this path is defensive-only (ListNeedingAnalysisAsync
                // already filters to STL/3MF), but if it ever happens this must match the upload
                // path's contract: an unsupported/unanalyzed format is unknown, not invalid.
                // TriangleCount still needs to become non-null so the row drops out of the
                // "still needs analysis" query and isn't retried on every backfill run.
                model.TriangleCount = 0;
                model.DimensionX = null;
                model.DimensionY = null;
                model.DimensionZ = null;
                return;
            }

            model.DimensionX = analysis.DimensionX;
            model.DimensionY = analysis.DimensionY;
            model.DimensionZ = analysis.DimensionZ;
            model.TriangleCount = analysis.TriangleCount ?? 0;
            model.IsValid = analysis.IsValid;
            model.ValidationErrors = analysis.ValidationErrors is { Count: > 0 } errors
                ? System.Text.Json.JsonSerializer.Serialize(errors)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ModelMetadataBackfill] Failed to analyze model {ModelId}; marking as unreadable", model.Id);
            MarkUnanalyzable(model, "Model could not be analyzed due to an unexpected error");
        }
    }

    private static void MarkUnanalyzable(Model3D model, string reason)
    {
        // TriangleCount must become non-null so this row drops out of the "still needs analysis"
        // query; IsValid=false reflects that geometry could not be confirmed at all — not a
        // printability judgment (see remarks on this class and #1811).
        model.TriangleCount = 0;
        model.DimensionX = null;
        model.DimensionY = null;
        model.DimensionZ = null;
        model.IsValid = false;
        model.ValidationErrors = System.Text.Json.JsonSerializer.Serialize(new[] { reason });
    }
}
