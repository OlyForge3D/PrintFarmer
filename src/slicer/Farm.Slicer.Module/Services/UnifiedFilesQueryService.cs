using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Tags;
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
    IStoredFileOperationsService fileOperations,
    ITagRepository tagRepository) : IUnifiedFilesQueryService
{
    private const int MaximumPageSize = 500;

    private readonly SlicerDbContext _slicerDb = slicerDb;
    private readonly AppDbContext _appDb = appDb;
    private readonly IStoredFileOperationsService _fileOperations = fileOperations;
    private readonly ITagRepository _tagRepository = tagRepository;

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
        string? modelProvider = _slicerDb.Database.ProviderName;
        string? gcodeProvider = _appDb.Database.ProviderName;
        if (!string.Equals(modelProvider, gcodeProvider, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unified file pagination requires both sources to use the same provider; found '{modelProvider}' and '{gcodeProvider}'.");
        }

        string modelNameCollation = GetBinaryNameCollation(modelProvider);
        string gcodeNameCollation = GetBinaryNameCollation(gcodeProvider);
        IComparer<string> nameComparer = GetBinaryNameComparer(modelProvider);
        IOrderedQueryable<Model3D> orderedModels = OrderModels(
            modelQuery,
            request.SortBy,
            request.SortOrder,
            modelNameCollation);
        IOrderedQueryable<GcodeFile> orderedGcodeFiles = OrderGcodeFiles(
            gcodeQuery,
            request.SortBy,
            request.SortOrder,
            gcodeNameCollation);

        SourcePartition partition = await FindPartitionAsync(
            orderedModels,
            modelSummary.Count,
            orderedGcodeFiles,
            gcodeSummary.Count,
            offset,
            request.SortBy,
            request.SortOrder,
            nameComparer,
            ct);
        Task<List<UnifiedFileCandidate>> modelCandidatesTask = QueryModelCandidatesAsync(
            orderedModels,
            partition.ModelOffset,
            pageSize,
            ct);
        Task<List<UnifiedFileCandidate>> gcodeCandidatesTask = QueryGcodeCandidatesAsync(
            orderedGcodeFiles,
            partition.GcodeOffset,
            pageSize,
            ct);
        await Task.WhenAll(modelCandidatesTask, gcodeCandidatesTask);
        List<UnifiedFileCandidate> pageCandidates = MergeSortedCandidates(
            await modelCandidatesTask,
            await gcodeCandidatesTask,
            request.SortBy,
            request.SortOrder,
            nameComparer,
            pageSize);

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
                model.FileFormat == ModelFileFormat.TMF ||
                model.FileFormat == ModelFileFormat.STL ||
                model.FileFormat == ModelFileFormat.STEP),
            UnifiedFileTypeFilter.Other => query.Where(model =>
                model.FileFormat != ModelFileFormat.TMF &&
                model.FileFormat != ModelFileFormat.STL &&
                model.FileFormat != ModelFileFormat.STEP),
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
                file.FileName.ToLower().EndsWith(".gcode") ||
                file.FileName.ToLower().EndsWith(".bgcode") ||
                file.FileName.ToLower().EndsWith(".gco") ||
                file.FileName.ToLower().EndsWith(".g") ||
                file.FileName.ToLower().EndsWith(".ngc") ||
                file.FileName.ToLower().EndsWith(".gc")),
            UnifiedFileTypeFilter.Other => query.Where(file =>
                !file.FileName.ToLower().EndsWith(".gcode") &&
                !file.FileName.ToLower().EndsWith(".bgcode") &&
                !file.FileName.ToLower().EndsWith(".gco") &&
                !file.FileName.ToLower().EndsWith(".g") &&
                !file.FileName.ToLower().EndsWith(".ngc") &&
                !file.FileName.ToLower().EndsWith(".gc")),
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

    private static IOrderedQueryable<Model3D> OrderModels(
        IQueryable<Model3D> query,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        string nameCollation)
    {
        bool descending = sortOrder == UnifiedFileSortOrder.Desc;
        return (sortBy, descending) switch
        {
            (UnifiedFileSortBy.Size, true) => query
                .OrderByDescending(model => model.FileSizeBytes)
                .ThenByDescending(model => EF.Functions.Collate(model.Name, nameCollation))
                .ThenByDescending(model => model.Id),
            (UnifiedFileSortBy.Size, false) => query
                .OrderBy(model => model.FileSizeBytes)
                .ThenBy(model => EF.Functions.Collate(model.Name, nameCollation))
                .ThenBy(model => model.Id),
            (UnifiedFileSortBy.Date, true) => query
                .OrderByDescending(model => model.UploadedAt)
                .ThenByDescending(model => EF.Functions.Collate(model.Name, nameCollation))
                .ThenByDescending(model => model.Id),
            (UnifiedFileSortBy.Date, false) => query
                .OrderBy(model => model.UploadedAt)
                .ThenBy(model => EF.Functions.Collate(model.Name, nameCollation))
                .ThenBy(model => model.Id),
            (UnifiedFileSortBy.Name, true) => query
                .OrderByDescending(model => EF.Functions.Collate(model.Name, nameCollation))
                .ThenByDescending(model => model.Id),
            _ => query
                .OrderBy(model => EF.Functions.Collate(model.Name, nameCollation))
                .ThenBy(model => model.Id),
        };
    }

    private static Task<List<UnifiedFileCandidate>> QueryModelCandidatesAsync(
        IOrderedQueryable<Model3D> orderedQuery,
        int skip,
        int take,
        CancellationToken ct)
    {
        return orderedQuery
            .Skip(skip)
            .Take(take)
            .Select(model => new UnifiedFileCandidate(
                UnifiedFileSource.Model,
                model.Id,
                model.Name,
                model.FileSizeBytes,
                model.UploadedAt))
            .ToListAsync(ct);
    }

    private static IOrderedQueryable<GcodeFile> OrderGcodeFiles(
        IQueryable<GcodeFile> query,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        string nameCollation)
    {
        bool descending = sortOrder == UnifiedFileSortOrder.Desc;
        return (sortBy, descending) switch
        {
            (UnifiedFileSortBy.Size, true) => query
                .OrderByDescending(file => file.FileSizeBytes)
                .ThenByDescending(file => EF.Functions.Collate(file.Name, nameCollation))
                .ThenByDescending(file => file.Id),
            (UnifiedFileSortBy.Size, false) => query
                .OrderBy(file => file.FileSizeBytes)
                .ThenBy(file => EF.Functions.Collate(file.Name, nameCollation))
                .ThenBy(file => file.Id),
            (UnifiedFileSortBy.Date, true) => query
                .OrderByDescending(file => file.UploadedAt)
                .ThenByDescending(file => EF.Functions.Collate(file.Name, nameCollation))
                .ThenByDescending(file => file.Id),
            (UnifiedFileSortBy.Date, false) => query
                .OrderBy(file => file.UploadedAt)
                .ThenBy(file => EF.Functions.Collate(file.Name, nameCollation))
                .ThenBy(file => file.Id),
            (UnifiedFileSortBy.Name, true) => query
                .OrderByDescending(file => EF.Functions.Collate(file.Name, nameCollation))
                .ThenByDescending(file => file.Id),
            _ => query
                .OrderBy(file => EF.Functions.Collate(file.Name, nameCollation))
                .ThenBy(file => file.Id),
        };
    }

    private static Task<List<UnifiedFileCandidate>> QueryGcodeCandidatesAsync(
        IOrderedQueryable<GcodeFile> orderedQuery,
        int skip,
        int take,
        CancellationToken ct)
    {
        return orderedQuery
            .Skip(skip)
            .Take(take)
            .Select(file => new UnifiedFileCandidate(
                UnifiedFileSource.Gcode,
                file.Id,
                file.Name,
                file.FileSizeBytes,
                file.UploadedAt))
            .ToListAsync(ct);
    }

    private static async Task<SourcePartition> FindPartitionAsync(
        IOrderedQueryable<Model3D> orderedModels,
        int modelCount,
        IOrderedQueryable<GcodeFile> orderedGcodeFiles,
        int gcodeCount,
        int offset,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        IComparer<string> nameComparer,
        CancellationToken ct)
    {
        int low = Math.Max(0, offset - gcodeCount);
        int high = Math.Min(offset, modelCount);

        while (low <= high)
        {
            int modelOffset = low + ((high - low) / 2);
            int gcodeOffset = offset - modelOffset;
            Task<CandidateBoundary> modelBoundaryTask = QueryModelBoundaryAsync(
                orderedModels,
                modelOffset,
                modelCount,
                ct);
            Task<CandidateBoundary> gcodeBoundaryTask = QueryGcodeBoundaryAsync(
                orderedGcodeFiles,
                gcodeOffset,
                gcodeCount,
                ct);
            await Task.WhenAll(modelBoundaryTask, gcodeBoundaryTask);

            CandidateBoundary modelBoundary = await modelBoundaryTask;
            CandidateBoundary gcodeBoundary = await gcodeBoundaryTask;
            if (modelBoundary.Left is not null &&
                gcodeBoundary.Right is not null &&
                CompareAcrossSources(
                    modelBoundary.Left,
                    gcodeBoundary.Right,
                    sortBy,
                    sortOrder,
                    nameComparer) > 0)
            {
                high = modelOffset - 1;
            }
            else if (gcodeBoundary.Left is not null &&
                     modelBoundary.Right is not null &&
                     CompareAcrossSources(
                         gcodeBoundary.Left,
                         modelBoundary.Right,
                         sortBy,
                         sortOrder,
                         nameComparer) > 0)
            {
                low = modelOffset + 1;
            }
            else
            {
                return new SourcePartition(modelOffset, gcodeOffset);
            }
        }

        throw new InvalidOperationException("Unable to locate the unified file page boundary.");
    }

    private static async Task<CandidateBoundary> QueryModelBoundaryAsync(
        IOrderedQueryable<Model3D> orderedQuery,
        int offset,
        int count,
        CancellationToken ct)
    {
        int skip = Math.Max(0, offset - 1);
        int take = offset > 0 && offset < count ? 2 : 1;
        List<UnifiedFileCandidate> candidates = await QueryModelCandidatesAsync(orderedQuery, skip, take, ct);
        return CreateBoundary(candidates, offset, count);
    }

    private static async Task<CandidateBoundary> QueryGcodeBoundaryAsync(
        IOrderedQueryable<GcodeFile> orderedQuery,
        int offset,
        int count,
        CancellationToken ct)
    {
        int skip = Math.Max(0, offset - 1);
        int take = offset > 0 && offset < count ? 2 : 1;
        List<UnifiedFileCandidate> candidates = await QueryGcodeCandidatesAsync(orderedQuery, skip, take, ct);
        return CreateBoundary(candidates, offset, count);
    }

    private static CandidateBoundary CreateBoundary(
        IReadOnlyList<UnifiedFileCandidate> candidates,
        int offset,
        int count)
    {
        UnifiedFileCandidate? left = offset > 0 && candidates.Count > 0 ? candidates[0] : null;
        UnifiedFileCandidate? right = offset < count && candidates.Count > 0 ? candidates[^1] : null;
        return new CandidateBoundary(left, right);
    }

    private static List<UnifiedFileCandidate> MergeSortedCandidates(
        IReadOnlyList<UnifiedFileCandidate> models,
        IReadOnlyList<UnifiedFileCandidate> gcodeFiles,
        UnifiedFileSortBy sortBy,
        UnifiedFileSortOrder sortOrder,
        IComparer<string> nameComparer,
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
            if (CompareAcrossSources(model, gcode, sortBy, sortOrder, nameComparer) <= 0)
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
        UnifiedFileSortOrder sortOrder,
        IComparer<string> nameComparer)
    {
        int comparison = sortBy switch
        {
            UnifiedFileSortBy.Size => left.FileSize.CompareTo(right.FileSize),
            UnifiedFileSortBy.Date => left.UploadedAt.CompareTo(right.UploadedAt),
            _ => nameComparer.Compare(left.DisplayName, right.DisplayName),
        };

        if (comparison == 0 && sortBy != UnifiedFileSortBy.Name)
        {
            comparison = nameComparer.Compare(left.DisplayName, right.DisplayName);
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
        IReadOnlyDictionary<Guid, IReadOnlyList<Tag>> modelTags =
            await _tagRepository.GetTagsByObjectsAsync(modelIds, "Model3D", ct);
        var items = new List<UnifiedFileDto>(candidates.Count);
        foreach (UnifiedFileCandidate candidate in candidates)
        {
            if (candidate.Source == UnifiedFileSource.Model &&
                models.TryGetValue(candidate.Id, out Model3D? model))
            {
                IReadOnlyList<Tag> tags = modelTags.TryGetValue(candidate.Id, out IReadOnlyList<Tag>? value)
                    ? value
                    : [];
                items.Add(MapModel(model, tags));
            }
            else if (candidate.Source == UnifiedFileSource.Gcode &&
                     gcodeFiles.TryGetValue(candidate.Id, out GcodeFile? gcodeFile))
            {
                items.Add(MapGcode(gcodeFile));
            }
        }

        return items;
    }

    private UnifiedFileDto MapModel(Model3D model, IReadOnlyList<Tag> tags)
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
            model.ThumbnailFileName is null ? null : _fileOperations.BuildModel3DThumbnailUrl(model.Id),
            MapTags(tags));
    }

    private UnifiedFileDto MapGcode(GcodeFile file)
    {
        IReadOnlyList<TagDto> tags = MapTags(file.Tags);

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

    private static List<TagDto> MapTags(IEnumerable<Tag> tags)
    {
        return tags
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
    }

    private static string GetBinaryNameCollation(string? providerName)
    {
        return providerName switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => "BINARY",
            "Microsoft.EntityFrameworkCore.SqlServer" => "Latin1_General_100_BIN2",
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "C",
            "Pomelo.EntityFrameworkCore.MySql" => "utf8mb4_bin",
            string provider => throw new NotSupportedException(
                $"Unified file pagination requires a binary name collation for provider '{provider}'."),
            null => throw new NotSupportedException(
                "Unified file pagination requires a relational provider with a binary name collation."),
        };
    }

    private static IComparer<string> GetBinaryNameComparer(string? providerName)
    {
        return providerName switch
        {
            "Microsoft.EntityFrameworkCore.SqlServer" => OrdinalPadSpaceComparer.Instance,
            "Microsoft.EntityFrameworkCore.Sqlite" or
            "Npgsql.EntityFrameworkCore.PostgreSQL" => UnicodeScalarComparer.Instance,
            "Pomelo.EntityFrameworkCore.MySql" => UnicodeScalarComparer.PadSpaceInstance,
            string provider => throw new NotSupportedException(
                $"Unified file pagination requires a binary name comparer for provider '{provider}'."),
            null => throw new NotSupportedException(
                "Unified file pagination requires a relational provider with a binary name comparer."),
        };
    }

    private sealed record UnifiedFileCandidate(
        UnifiedFileSource Source,
        Guid Id,
        string DisplayName,
        long FileSize,
        DateTime UploadedAt);

    private sealed record CandidateBoundary(
        UnifiedFileCandidate? Left,
        UnifiedFileCandidate? Right);

    private sealed record SourcePartition(int ModelOffset, int GcodeOffset);

    private sealed record SourceSummary(int Count, long Size)
    {
        public static SourceSummary Empty { get; } = new(0, 0);
    }

    private sealed class UnicodeScalarComparer : IComparer<string>
    {
        private readonly bool _trimTrailingSpaces;

        private UnicodeScalarComparer(bool trimTrailingSpaces)
        {
            _trimTrailingSpaces = trimTrailingSpaces;
        }

        public static UnicodeScalarComparer Instance { get; } = new(false);

        public static UnicodeScalarComparer PadSpaceInstance { get; } = new(true);

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            if (_trimTrailingSpaces)
            {
                left = left.TrimEnd(' ');
                right = right.TrimEnd(' ');
            }

            var leftRunes = left.EnumerateRunes().GetEnumerator();
            var rightRunes = right.EnumerateRunes().GetEnumerator();
            while (true)
            {
                bool hasLeft = leftRunes.MoveNext();
                bool hasRight = rightRunes.MoveNext();
                if (!hasLeft || !hasRight)
                {
                    return hasLeft.CompareTo(hasRight);
                }

                int comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
        }
    }

    private sealed class OrdinalPadSpaceComparer : IComparer<string>
    {
        public static OrdinalPadSpaceComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return string.CompareOrdinal(left.TrimEnd(' '), right.TrimEnd(' '));
        }
    }
}
