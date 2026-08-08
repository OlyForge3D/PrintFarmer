using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Builds an exact page over independently stored 3D-model and G-code records.
/// </summary>
public sealed class UnifiedFilesQueryService(
    SlicerDbContext slicerDb,
    AppDbContext appDb,
    IStoredFileOperationsService fileOperations) : IUnifiedFilesQueryService
{
    private const int MaximumPageSize = 500;

    private readonly SlicerDbContext _slicerDb = slicerDb;
    private readonly AppDbContext _appDb = appDb;
    private readonly IStoredFileOperationsService _fileOperations = fileOperations;

    /// <inheritdoc/>
    public async Task<UnifiedFilesQueryResponse> QueryAsync(
        UnifiedFilesQueryRequestDto request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        int requestedPage = Math.Max(1, request.Page);
        int pageSize = Math.Clamp(request.PageSize, 1, MaximumPageSize);

        IQueryable<Model3D> modelQuery = BuildModelQuery(request);
        IQueryable<GcodeFile> gcodeQuery = BuildGcodeQuery(request);

        Task<SourceSummary> modelSummaryTask = GetModelSummaryAsync(modelQuery, ct);
        Task<SourceSummary> gcodeSummaryTask = GetGcodeSummaryAsync(gcodeQuery, ct);
        await Task.WhenAll(modelSummaryTask, gcodeSummaryTask);

        SourceSummary modelSummary = await modelSummaryTask;
        SourceSummary gcodeSummary = await gcodeSummaryTask;
        int totalItems = checked(modelSummary.Count + gcodeSummary.Count);
        long totalSize = checked(modelSummary.Size + gcodeSummary.Size);
        int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalItems / pageSize));
        int page = Math.Min(requestedPage, totalPages);
        int offset = checked((page - 1) * pageSize);
        int mergeWindowSize = checked(offset + pageSize);

        Task<List<UnifiedFileCandidate>> modelCandidatesTask = QueryModelCandidatesAsync(
            modelQuery,
            request.SortBy,
            request.SortOrder,
            mergeWindowSize,
            ct);
        Task<List<UnifiedFileCandidate>> gcodeCandidatesTask = QueryGcodeCandidatesAsync(
            gcodeQuery,
            request.SortBy,
            request.SortOrder,
            mergeWindowSize,
            ct);
        await Task.WhenAll(modelCandidatesTask, gcodeCandidatesTask);

        List<UnifiedFileCandidate> mergedPrefix = MergeSortedCandidates(
            await modelCandidatesTask,
            await gcodeCandidatesTask,
            request.SortBy,
            request.SortOrder,
            mergeWindowSize);
        List<UnifiedFileCandidate> pageCandidates = mergedPrefix
            .Skip(offset)
            .Take(pageSize)
            .ToList();

        IReadOnlyList<UnifiedFileDto> items = await HydratePageAsync(pageCandidates, ct);
        return new UnifiedFilesQueryResponse(items, totalItems, totalSize, page, pageSize, totalPages);
    }

#pragma warning disable CA1862 // EF Core does not translate the StringComparison overloads used by this rule.
    private IQueryable<Model3D> BuildModelQuery(UnifiedFilesQueryRequestDto request)
    {
        IQueryable<Model3D> query = _slicerDb.Models3D
            .AsNoTracking()
            .Where(model => model.IsValid);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string search = request.Search.Trim().ToLower();
            query = query.Where(model =>
                model.Name.ToLower().Contains(search) ||
                model.FileName.ToLower().Contains(search));
        }

        query = request.Filter switch
        {
            UnifiedFileTypeFilter.Gcode => query.Where(_ => false),
            UnifiedFileTypeFilter.Models => query.Where(model =>
                model.Name.ToLower().EndsWith(".3mf") ||
                model.Name.ToLower().EndsWith(".stl") ||
                model.Name.ToLower().EndsWith(".step") ||
                model.Name.ToLower().EndsWith(".stp")),
            UnifiedFileTypeFilter.Other => query.Where(model =>
                !model.Name.ToLower().EndsWith(".3mf") &&
                !model.Name.ToLower().EndsWith(".stl") &&
                !model.Name.ToLower().EndsWith(".step") &&
                !model.Name.ToLower().EndsWith(".stp")),
            _ => query,
        };

        return query;
    }

    private IQueryable<GcodeFile> BuildGcodeQuery(UnifiedFilesQueryRequestDto request)
    {
        IQueryable<GcodeFile> query = _appDb.GcodeFiles.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string search = request.Search.Trim().ToLower();
            query = query.Where(file =>
                file.Name.ToLower().Contains(search) ||
                file.FileName.ToLower().Contains(search));
        }

        if (request.PrinterId.HasValue)
        {
            query = query.Where(file => file.SourcePrinterId == request.PrinterId.Value);
        }

        if (request.HarvestId.HasValue)
        {
            query = query.Where(file => file.HarvestFileMappings.Any(
                mapping => mapping.HarvestDiscoveredFile.HarvestOperationId == request.HarvestId.Value));
        }

        query = request.Filter switch
        {
            UnifiedFileTypeFilter.Models => query.Where(_ => false),
            UnifiedFileTypeFilter.Gcode => query.Where(file =>
                file.Name.ToLower().EndsWith(".gcode") ||
                file.Name.ToLower().EndsWith(".gco") ||
                file.Name.ToLower().EndsWith(".g") ||
                file.Name.ToLower().EndsWith(".ngc") ||
                file.Name.ToLower().EndsWith(".gc")),
            UnifiedFileTypeFilter.Other => query.Where(file =>
                !file.Name.ToLower().EndsWith(".gcode") &&
                !file.Name.ToLower().EndsWith(".gco") &&
                !file.Name.ToLower().EndsWith(".g") &&
                !file.Name.ToLower().EndsWith(".ngc") &&
                !file.Name.ToLower().EndsWith(".gc")),
            _ => query,
        };

        return query;
    }
#pragma warning restore CA1862

    private static async Task<SourceSummary> GetModelSummaryAsync(
        IQueryable<Model3D> query,
        CancellationToken ct)
    {
        SourceSummary? summary = await query
            .GroupBy(_ => 1)
            .Select(group => new SourceSummary(group.Count(), group.Sum(model => model.FileSizeBytes)))
            .SingleOrDefaultAsync(ct);
        return summary ?? SourceSummary.Empty;
    }

    private static async Task<SourceSummary> GetGcodeSummaryAsync(
        IQueryable<GcodeFile> query,
        CancellationToken ct)
    {
        SourceSummary? summary = await query
            .GroupBy(_ => 1)
            .Select(group => new SourceSummary(group.Count(), group.Sum(file => file.FileSizeBytes)))
            .SingleOrDefaultAsync(ct);
        return summary ?? SourceSummary.Empty;
    }

    private static Task<List<UnifiedFileCandidate>> QueryModelCandidatesAsync(
        IQueryable<Model3D> query,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        int take,
        CancellationToken ct)
    {
        bool descending = sortOrder == UnifiedFileSortOrder.Desc;
        IOrderedQueryable<Model3D> orderedQuery = (sortBy, descending) switch
        {
            (UnifiedFileSortBy.Size, true) => query
                .OrderByDescending(model => model.FileSizeBytes)
                .ThenByDescending(model => model.Name)
                .ThenByDescending(model => model.Id),
            (UnifiedFileSortBy.Size, false) => query
                .OrderBy(model => model.FileSizeBytes)
                .ThenBy(model => model.Name)
                .ThenBy(model => model.Id),
            (UnifiedFileSortBy.Date, true) => query
                .OrderByDescending(model => model.UploadedAt)
                .ThenByDescending(model => model.Name)
                .ThenByDescending(model => model.Id),
            (UnifiedFileSortBy.Date, false) => query
                .OrderBy(model => model.UploadedAt)
                .ThenBy(model => model.Name)
                .ThenBy(model => model.Id),
            (UnifiedFileSortBy.Name, true) => query
                .OrderByDescending(model => model.Name)
                .ThenByDescending(model => model.Id),
            _ => query
                .OrderBy(model => model.Name)
                .ThenBy(model => model.Id),
        };

        return orderedQuery
            .Take(take)
            .Select(model => new UnifiedFileCandidate(
                UnifiedFileSource.Model,
                model.Id,
                model.Name,
                model.FileSizeBytes,
                model.UploadedAt))
            .ToListAsync(ct);
    }

    private static Task<List<UnifiedFileCandidate>> QueryGcodeCandidatesAsync(
        IQueryable<GcodeFile> query,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        int take,
        CancellationToken ct)
    {
        bool descending = sortOrder == UnifiedFileSortOrder.Desc;
        IOrderedQueryable<GcodeFile> orderedQuery = (sortBy, descending) switch
        {
            (UnifiedFileSortBy.Size, true) => query
                .OrderByDescending(file => file.FileSizeBytes)
                .ThenByDescending(file => file.Name)
                .ThenByDescending(file => file.Id),
            (UnifiedFileSortBy.Size, false) => query
                .OrderBy(file => file.FileSizeBytes)
                .ThenBy(file => file.Name)
                .ThenBy(file => file.Id),
            (UnifiedFileSortBy.Date, true) => query
                .OrderByDescending(file => file.UploadedAt)
                .ThenByDescending(file => file.Name)
                .ThenByDescending(file => file.Id),
            (UnifiedFileSortBy.Date, false) => query
                .OrderBy(file => file.UploadedAt)
                .ThenBy(file => file.Name)
                .ThenBy(file => file.Id),
            (UnifiedFileSortBy.Name, true) => query
                .OrderByDescending(file => file.Name)
                .ThenByDescending(file => file.Id),
            _ => query
                .OrderBy(file => file.Name)
                .ThenBy(file => file.Id),
        };

        return orderedQuery
            .Take(take)
            .Select(file => new UnifiedFileCandidate(
                UnifiedFileSource.Gcode,
                file.Id,
                file.Name,
                file.FileSizeBytes,
                file.UploadedAt))
            .ToListAsync(ct);
    }

    private static List<UnifiedFileCandidate> MergeSortedCandidates(
        IReadOnlyList<UnifiedFileCandidate> models,
        IReadOnlyList<UnifiedFileCandidate> gcodeFiles,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        int take)
    {
        var merged = new List<UnifiedFileCandidate>(Math.Min(take, models.Count + gcodeFiles.Count));
        int modelIndex = 0;
        int gcodeIndex = 0;

        while (merged.Count < take && (modelIndex < models.Count || gcodeIndex < gcodeFiles.Count))
        {
            if (modelIndex >= models.Count)
            {
                merged.Add(gcodeFiles[gcodeIndex++]);
                continue;
            }

            if (gcodeIndex >= gcodeFiles.Count)
            {
                merged.Add(models[modelIndex++]);
                continue;
            }

            UnifiedFileCandidate model = models[modelIndex];
            UnifiedFileCandidate gcode = gcodeFiles[gcodeIndex];
            if (CompareAcrossSources(model, gcode, sortBy, sortOrder) <= 0)
            {
                merged.Add(model);
                modelIndex++;
            }
            else
            {
                merged.Add(gcode);
                gcodeIndex++;
            }
        }

        return merged;
    }

    private static int CompareAcrossSources(
        UnifiedFileCandidate left,
        UnifiedFileCandidate right,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder)
    {
        int comparison = sortBy switch
        {
            UnifiedFileSortBy.Size => left.FileSize.CompareTo(right.FileSize),
            UnifiedFileSortBy.Date => left.UploadedAt.CompareTo(right.UploadedAt),
            _ => string.Compare(left.DisplayName, right.DisplayName, StringComparison.Ordinal),
        };

        if (comparison == 0 && sortBy != UnifiedFileSortBy.Name)
        {
            comparison = string.Compare(left.DisplayName, right.DisplayName, StringComparison.Ordinal);
        }

        if (comparison == 0)
        {
            comparison = left.Source.CompareTo(right.Source);
        }

        return sortOrder == UnifiedFileSortOrder.Desc ? -comparison : comparison;
    }

    private async Task<IReadOnlyList<UnifiedFileDto>> HydratePageAsync(
        IReadOnlyList<UnifiedFileCandidate> candidates,
        CancellationToken ct)
    {
        Guid[] modelIds = candidates
            .Where(candidate => candidate.Source == UnifiedFileSource.Model)
            .Select(candidate => candidate.Id)
            .ToArray();
        Guid[] gcodeIds = candidates
            .Where(candidate => candidate.Source == UnifiedFileSource.Gcode)
            .Select(candidate => candidate.Id)
            .ToArray();

        Task<Dictionary<Guid, Model3D>> modelsTask = _slicerDb.Models3D
            .AsNoTracking()
            .Where(model => modelIds.Contains(model.Id))
            .ToDictionaryAsync(model => model.Id, ct);
        Task<Dictionary<Guid, GcodeFile>> gcodeTask = _appDb.GcodeFiles
            .AsNoTrackingWithIdentityResolution()
            .Where(file => gcodeIds.Contains(file.Id))
            .Include(file => file.Tags)
            .Include(file => file.PrinterModel)
            .ToDictionaryAsync(file => file.Id, ct);
        await Task.WhenAll(modelsTask, gcodeTask);

        Dictionary<Guid, Model3D> models = await modelsTask;
        Dictionary<Guid, GcodeFile> gcodeFiles = await gcodeTask;
        var items = new List<UnifiedFileDto>(candidates.Count);
        foreach (UnifiedFileCandidate candidate in candidates)
        {
            items.Add(candidate.Source == UnifiedFileSource.Model
                ? MapModel(models[candidate.Id])
                : MapGcode(gcodeFiles[candidate.Id]));
        }

        return items;
    }

    private UnifiedFileDto MapModel(Model3D model)
    {
        string fileType = model.FileFormat == ModelFileFormat.TMF
            ? "3mf"
            : model.FileFormat.ToString().ToLowerInvariant();
        return new UnifiedFileDto(
            UnifiedFileSource.Model,
            model.Id,
            model.FilePath,
            model.Name,
            model.FileName,
            model.FileSizeBytes,
            fileType,
            model.UploadedAt,
            _fileOperations.BuildModel3DFileUrl(model.Id, model.FileFormat),
            model.ThumbnailFileName is null ? null : _fileOperations.BuildModel3DThumbnailUrl(model.Id));
    }

    private UnifiedFileDto MapGcode(GcodeFile file)
    {
        IReadOnlyList<TagDto> tags = file.Tags
            .Select(tag => new TagDto
            {
                Id = tag.Id,
                Name = tag.Name,
                Category = tag.Category,
                IsAutoGenerated = tag.IsAutoGenerated,
                Color = tag.Color,
                Description = tag.Description,
                Revision = tag.Revision,
                ConcurrencyToken = tag.ConcurrencyToken,
            })
            .ToList();

        return new UnifiedFileDto(
            UnifiedFileSource.Gcode,
            file.Id,
            file.FilePath,
            file.Name,
            file.FileName,
            file.FileSizeBytes,
            file.FileType,
            file.UploadedAt,
            _fileOperations.BuildGcodeFileUrl(file.Id),
            file.ThumbnailFileName is null ? null : _fileOperations.BuildGcodeThumbnailUrl(file.Id),
            tags,
            file.RequiredMaterial,
            file.SlicerName,
            file.SlicerVersion,
            file.EstimatedPrintTimeMinutes,
            file.EstimatedFilamentLengthMm,
            file.RequiredNozzleDiameter,
            file.RequiredMaterial,
            file.PrinterModel?.Name,
            file.ExtractedPrinterModelName,
            file.LayerHeight,
            file.InfillPercentage,
            file.Perimeters,
            file.PrintTemperature,
            file.BedTemperature);
    }

    private sealed record UnifiedFileCandidate(
        UnifiedFileSource Source,
        Guid Id,
        string DisplayName,
        long FileSize,
        DateTime UploadedAt);

    private sealed record SourceSummary(int Count, long Size)
    {
        public static SourceSummary Empty { get; } = new(0, 0);
    }
}
