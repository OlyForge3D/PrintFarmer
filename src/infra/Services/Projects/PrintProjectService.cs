using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Projects;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.PrinterGroups;
using Farm.Infrastructure.Services.Projects;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Projects;

/// <summary>
/// Service implementation for managing print projects.
/// </summary>
public class PrintProjectService(
    AppDbContext db,
    ILogger<PrintProjectService> logger,
    IJobQueueService queueService,
    ISpoolmanService spoolmanService,
    IPrinterGroupService printerGroupService) : IPrintProjectService
{
    public async Task<IReadOnlyList<PrintProjectListDto>> GetProjectsAsync(
        PrintProjectStatus? status = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var query = db.PrintProjects
            .Include(p => p.Files)
            .AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(p => EF.Functions.Like(p.Name, $"%{search}%"));
        }

        var projects = await query
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return projects.Select(p => new PrintProjectListDto(
            p.Id,
            p.Name,
            p.Description,
            p.Status,
            p.Priority,
            p.DueDate,
            p.Files.Count,
            p.Files.Count(f => f.PrintedCount >= f.PrintCount),
            p.Files.Sum(f => f.PrintCount),
            p.Files.Sum(f => f.PrintedCount),
            EstimatedTotalCost: null,
            CompletedCost: null,
            p.CreatedAt,
            p.CompletedAt)).ToList();
    }

    public async Task<PrintProjectDetailDto?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await db.PrintProjects
            .Include(p => p.Files)
                .ThenInclude(f => f.GcodeFile)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return null;
        }

        return MapToDetailDto(project);
    }

    public async Task<PrintProjectDetailDto> CreateProjectAsync(CreatePrintProjectRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var project = new PrintProject
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Priority = request.Priority,
            DueDate = request.DueDate.HasValue ? DateTime.SpecifyKind(request.DueDate.Value, DateTimeKind.Utc) : null,
            Notes = request.Notes,
            Status = PrintProjectStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PrintProjects.Add(project);

        // Add initial files if provided
        if (request.Files?.Count > 0)
        {
            var fileIds = request.Files.Select(f => f.GcodeFileId).ToList();
            var existingFiles = await db.GcodeFiles
                .Where(g => fileIds.Contains(g.Id))
                .Select(g => g.Id)
                .ToListAsync(ct);

            var sortOrder = 0;
            foreach (var fileRequest in request.Files)
            {
                if (!existingFiles.Contains(fileRequest.GcodeFileId))
                {
                    logger.LogWarning("Skipping non-existent gcode file {FileRequestGcodeFileId} when creating project", fileRequest.GcodeFileId);
                    continue;
                }

                var projectFile = new PrintProjectFile
                {
                    Id = Guid.NewGuid(),
                    PrintProjectId = project.Id,
                    GcodeFileId = fileRequest.GcodeFileId,
                    SpoolmanFilamentId = fileRequest.SpoolmanFilamentId,
                    MaterialRequirement = fileRequest.MaterialRequirement,
                    PrintCount = Math.Max(1, fileRequest.PrintCount),
                    PrintedCount = 0,
                    Status = PrintProjectFileStatus.Pending,
                    SortOrder = sortOrder++,
                    Notes = fileRequest.Notes,
                    PlateIndex = fileRequest.PlateIndex,
                    PlateName = fileRequest.PlateName,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.PrintProjectFiles.Add(projectFile);
            }
        }

        await db.SaveChangesAsync(ct);

        // Reload with navigation properties
        return (await GetProjectAsync(project.Id, ct))!;
    }

    public async Task<PrintProjectDetailDto?> UpdateProjectAsync(Guid projectId, UpdatePrintProjectRequest request, CancellationToken ct = default)
    {
        var project = await db.PrintProjects.FindAsync([projectId], ct);
        if (project is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        if (request.Name is not null)
        {
            project.Name = request.Name;
        }

        if (request.Description is not null)
        {
            project.Description = request.Description;
        }

        if (request.Status.HasValue)
        {
            project.Status = request.Status.Value;
            if (request.Status.Value == PrintProjectStatus.Completed)
            {
                project.CompletedAt = now;
            }
        }

        if (request.Priority.HasValue)
        {
            project.Priority = request.Priority.Value;
        }

        if (request.DueDate.HasValue)
        {
            project.DueDate = DateTime.SpecifyKind(request.DueDate.Value, DateTimeKind.Utc);
        }

        if (request.Notes is not null)
        {
            project.Notes = request.Notes;
        }

        project.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return await GetProjectAsync(projectId, ct);
    }

    public async Task<bool> DeleteProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await db.PrintProjects.FindAsync([projectId], ct);
        if (project is null)
        {
            return false;
        }

        db.PrintProjects.Remove(project);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PrintProjectFileDto>> AddFilesToProjectAsync(
        Guid projectId,
        IReadOnlyList<AddFileToProjectRequest> files,
        CancellationToken ct = default)
    {
        var project = await db.PrintProjects
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            throw new KeyNotFoundException($"Project {projectId} not found");
        }

        var now = DateTime.UtcNow;
        var fileIds = files.Select(f => f.GcodeFileId).ToList();
        var existingGcodeFiles = await db.GcodeFiles
            .Where(g => fileIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var existingProjectFileIds = project.Files.Select(f => f.GcodeFileId).ToHashSet();
        var maxSortOrder = project.Files.Count > 0 ? project.Files.Max(f => f.SortOrder) : -1;

        var addedFiles = new List<PrintProjectFile>();

        foreach (var fileRequest in files)
        {
            // Skip if file doesn't exist or already in project
            if (!existingGcodeFiles.ContainsKey(fileRequest.GcodeFileId))
            {
                logger.LogWarning("Skipping non-existent gcode file {FileRequestGcodeFileId}", fileRequest.GcodeFileId);
                continue;
            }

            if (existingProjectFileIds.Contains(fileRequest.GcodeFileId))
            {
                logger.LogWarning("Skipping duplicate gcode file {FileRequestGcodeFileId} already in project", fileRequest.GcodeFileId);
                continue;
            }

            var projectFile = new PrintProjectFile
            {
                Id = Guid.NewGuid(),
                PrintProjectId = projectId,
                GcodeFileId = fileRequest.GcodeFileId,
                SpoolmanFilamentId = fileRequest.SpoolmanFilamentId,
                MaterialRequirement = fileRequest.MaterialRequirement,
                PrintCount = Math.Max(1, fileRequest.PrintCount),
                PrintedCount = 0,
                Status = PrintProjectFileStatus.Pending,
                SortOrder = ++maxSortOrder,
                Notes = fileRequest.Notes,
                PlateIndex = fileRequest.PlateIndex,
                PlateName = fileRequest.PlateName,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.PrintProjectFiles.Add(projectFile);
            addedFiles.Add(projectFile);
            existingProjectFileIds.Add(fileRequest.GcodeFileId);
        }

        if (addedFiles.Count > 0)
        {
            project.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        // Reload to get gcode file info
        var addedFileIds = addedFiles.Select(f => f.Id).ToList();
        var reloadedFiles = await db.PrintProjectFiles
            .Include(f => f.GcodeFile)
            .Where(f => addedFileIds.Contains(f.Id))
            .ToListAsync(ct);

        return reloadedFiles.Select(MapToFileDto).ToList();
    }

    public async Task<bool> RemoveFileFromProjectAsync(Guid projectId, Guid fileId, CancellationToken ct = default)
    {
        var projectFile = await db.PrintProjectFiles
            .FirstOrDefaultAsync(f => f.PrintProjectId == projectId && f.Id == fileId, ct);

        if (projectFile is null)
        {
            return false;
        }

        db.PrintProjectFiles.Remove(projectFile);

        var project = await db.PrintProjects.FindAsync([projectId], ct);
        if (project is not null)
        {
            project.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PrintProjectFileDto?> UpdateProjectFileAsync(
        Guid projectId,
        Guid fileId,
        UpdateProjectFileRequest request,
        CancellationToken ct = default)
    {
        var projectFile = await db.PrintProjectFiles
            .Include(f => f.GcodeFile)
            .FirstOrDefaultAsync(f => f.PrintProjectId == projectId && f.Id == fileId, ct);

        if (projectFile is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        if (request.SpoolmanFilamentId.HasValue)
        {
            // Allow clearing by sending 0 or by having a value
            projectFile.SpoolmanFilamentId = request.SpoolmanFilamentId.Value == 0 ? null : request.SpoolmanFilamentId.Value;
        }

        if (request.MaterialRequirement is not null)
        {
            projectFile.MaterialRequirement = request.MaterialRequirement;
        }

        if (request.PrintCount.HasValue)
        {
            projectFile.PrintCount = Math.Max(1, request.PrintCount.Value);
        }

        if (request.PrintedCount.HasValue)
        {
            projectFile.PrintedCount = Math.Max(0, request.PrintedCount.Value);
        }

        if (request.Status.HasValue)
        {
            projectFile.Status = request.Status.Value;
        }

        if (request.SortOrder.HasValue)
        {
            projectFile.SortOrder = request.SortOrder.Value;
        }

        if (request.Notes is not null)
        {
            projectFile.Notes = request.Notes;
        }

        if (request.PlateIndex.HasValue)
        {
            projectFile.PlateIndex = request.PlateIndex.Value == -1 ? null : request.PlateIndex.Value;
        }

        if (request.PlateName is not null)
        {
            projectFile.PlateName = string.IsNullOrEmpty(request.PlateName) ? null : request.PlateName;
        }

        projectFile.UpdatedAt = now;

        // Auto-update status based on completion
        if (projectFile.PrintedCount >= projectFile.PrintCount)
        {
            projectFile.Status = PrintProjectFileStatus.Completed;
        }

        // Update project timestamp
        var project = await db.PrintProjects.FindAsync([projectId], ct);
        if (project is not null)
        {
            project.UpdatedAt = now;
            await UpdateProjectStatusIfNeededAsync(project, ct);
        }

        await db.SaveChangesAsync(ct);
        return MapToFileDto(projectFile);
    }

    public async Task<PrintProjectFileDto?> MarkFilePrintedAsync(
        Guid projectId,
        Guid fileId,
        Guid? printJobId = null,
        CancellationToken ct = default)
    {
        var projectFile = await db.PrintProjectFiles
            .Include(f => f.GcodeFile)
            .FirstOrDefaultAsync(f => f.PrintProjectId == projectId && f.Id == fileId, ct);

        if (projectFile is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        projectFile.PrintedCount++;
        projectFile.LastPrintedAt = now;
        projectFile.LastPrintJobId = printJobId;
        projectFile.UpdatedAt = now;

        // Auto-update status
        if (projectFile.PrintedCount >= projectFile.PrintCount)
        {
            projectFile.Status = PrintProjectFileStatus.Completed;
        }
        else if (projectFile.PrintedCount > 0)
        {
            projectFile.Status = PrintProjectFileStatus.Printing;
        }

        // Update project
        var project = await db.PrintProjects.FindAsync([projectId], ct);
        if (project is not null)
        {
            project.UpdatedAt = now;
            await UpdateProjectStatusIfNeededAsync(project, ct);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Marked file {FileId} as printed ({ProjectFilePrintedCount}/{ProjectFilePrintCount}) in project {ProjectId}", fileId, projectFile.PrintedCount, projectFile.PrintCount, projectId);

        return MapToFileDto(projectFile);
    }

    public async Task<QueueProjectResultDto?> QueueProjectAsync(Guid projectId, QueueProjectRequest request, Guid? userId = null, CancellationToken ct = default)
    {
        var project = await db.PrintProjects
            .Include(p => p.Files)
                .ThenInclude(f => f.GcodeFile)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return null;
        }

        // Get only pending/incomplete files
        var pendingFiles = project.Files
            .Where(f => f.Status != PrintProjectFileStatus.Completed && f.Status != PrintProjectFileStatus.Skipped)
            .Where(f => f.PrintedCount < f.PrintCount)
            .ToList();

        if (pendingFiles.Count == 0)
        {
            return new QueueProjectResultDto(projectId, project.Name, 0, 0, 0, []);
        }

        // Fetch Spoolman filament data for color grouping (best-effort)
        Dictionary<int, SpoolmanFilamentDto> filamentLookup = [];
        if (request.GroupByColor)
        {
            try
            {
                var filaments = await spoolmanService.ListFilamentsAsync(ct);
                filamentLookup = filaments.ToDictionary(f => f.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not fetch Spoolman filaments for project queue ordering: {Message}", ex.Message);
            }
        }

        // Smart ordering: group by material type, then by color
        var orderedFiles = OrderFilesForPrinting(pendingFiles, filamentLookup, request.GroupByMaterial, request.GroupByColor);

        // Pre-validate ACL for all files before enqueueing any, to avoid partial enqueue + 403
        if (userId.HasValue)
        {
            var groupIds = orderedFiles
                .Where(f => f.GcodeFile?.PrinterGroupId.HasValue == true)
                .Select(f => f.GcodeFile!.PrinterGroupId!.Value)
                .Distinct();

            foreach (var groupId in groupIds)
            {
                bool canSubmit = await printerGroupService.CanUserSubmitToGroupAsync(groupId, userId.Value, ct);
                if (!canSubmit)
                {
                    throw new QueueGroupAccessDeniedException(groupId, userId.Value);
                }
            }
        }

        var queuedFiles = new List<QueuedProjectFileDto>();
        var queueOrder = 0;

        foreach (var file in orderedFiles)
        {
            var remainingPrints = file.PrintCount - file.PrintedCount;

            // Queue one job per file with Copies = remaining prints
            var materialType = file.MaterialRequirement ?? file.GcodeFile?.RequiredMaterial;
            string? colorHex = null;
            string? filVendor = null;
            string? filName = null;
            if (file.SpoolmanFilamentId.HasValue && filamentLookup.TryGetValue(file.SpoolmanFilamentId.Value, out var filament))
            {
                colorHex = filament.ColorHex;
                filVendor = filament.Vendor;
                filName = filament.Name;
            }

            var queueRequest = new QueuePrintJobDto
            {
                GcodeFileId = file.GcodeFileId,
                AssignedPrinterId = request.AssignedPrinterId,
                Priority = (PrintJobPriority)request.Priority,
                RequiredMaterialType = materialType,
                RequiredNozzleDiameter = file.GcodeFile?.RequiredNozzleDiameter.HasValue == true
                    ? (decimal)file.GcodeFile.RequiredNozzleDiameter.Value
                    : null,
                RequiredPrinterModel = file.GcodeFile?.ExtractedPrinterModelName,
                ProjectId = projectId,
                ProjectName = project.Name,
                SpoolmanFilamentId = file.SpoolmanFilamentId,
                FilamentName = filName,
                FilamentVendor = filVendor,
                FilamentColor = colorHex,
                Copies = remainingPrints,
                ProjectFileId = file.Id,
                PlateIndex = file.PlateIndex,
                PlateName = file.PlateName,
            };

            var job = await queueService.AddJobToQueueAsync(queueRequest, userId, ct);
            if (job is not null)
            {
                queuedFiles.Add(new QueuedProjectFileDto(
                    file.Id,
                    job.Id,
                    file.GcodeFile?.FileName ?? "Unknown",
                    materialType,
                    colorHex,
                    remainingPrints,
                    file.GcodeFile?.EstimatedPrintTimeMinutes,
                    queueOrder++));
            }
            else
            {
                logger.LogWarning("Could not queue file {FileGcodeFileId} (no compatible printer available)", file.GcodeFileId);
            }
        }

        // Update project status to InProgress
        if (queuedFiles.Count > 0 && project.Status == PrintProjectStatus.Open)
        {
            project.Status = PrintProjectStatus.InProgress;
            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var totalEstimatedMinutes = queuedFiles
            .Where(f => f.EstimatedPrintTimeMinutes.HasValue)
            .Sum(f => f.EstimatedPrintTimeMinutes!.Value);

        logger.LogInformation("Queued {QueuedFilesCount} jobs from project {ProjectId} ({ProjectName})", queuedFiles.Count, projectId, project.Name);

        return new QueueProjectResultDto(
            projectId,
            project.Name,
            queuedFiles.Count,
            queuedFiles.Sum(f => f.PrintCount),
            totalEstimatedMinutes > 0 ? totalEstimatedMinutes : null,
            queuedFiles);
    }

    /// <summary>
    /// Orders project files for optimal printing by grouping by material type and color
    /// to minimize filament swaps.
    /// </summary>
    private static List<PrintProjectFile> OrderFilesForPrinting(
        List<PrintProjectFile> files,
        Dictionary<int, SpoolmanFilamentDto> filamentLookup,
        bool groupByMaterial,
        bool groupByColor)
    {
        IEnumerable<PrintProjectFile> ordered;

        if (groupByMaterial && groupByColor)
        {
            // Primary: material type, Secondary: vendor, Tertiary: color, Quaternary: sort order
            ordered = files.OrderBy(f => f.MaterialRequirement ?? f.GcodeFile?.RequiredMaterial ?? "zzz")
                .ThenBy(f =>
                {
                    if (f.SpoolmanFilamentId.HasValue && filamentLookup.TryGetValue(f.SpoolmanFilamentId.Value, out var filament))
                    {
                        return filament.Vendor ?? "zzz";
                    }

                    return "zzz";
                })
                .ThenBy(f =>
                {
                    if (f.SpoolmanFilamentId.HasValue && filamentLookup.TryGetValue(f.SpoolmanFilamentId.Value, out var filament))
                    {
                        return filament.ColorHex ?? "zzz";
                    }

                    return "zzz";
                })
                .ThenBy(f => f.SortOrder);
        }
        else if (groupByMaterial)
        {
            ordered = files.OrderBy(f => f.MaterialRequirement ?? f.GcodeFile?.RequiredMaterial ?? "zzz")
                .ThenBy(f => f.SortOrder);
        }
        else if (groupByColor)
        {
            ordered = files.OrderBy(f =>
            {
                if (f.SpoolmanFilamentId.HasValue && filamentLookup.TryGetValue(f.SpoolmanFilamentId.Value, out var filament))
                {
                    return filament.ColorHex ?? "zzz";
                }

                return "zzz";
            })
                .ThenBy(f => f.SortOrder);
        }
        else
        {
            ordered = files.OrderBy(f => f.SortOrder);
        }

        return ordered.ToList();
    }

    public async Task<PrintProjectProgressDto?> GetProjectProgressAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await db.PrintProjects
            .Include(p => p.Files)
                .ThenInclude(f => f.GcodeFile)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return null;
        }

        var totalPrints = project.Files.Sum(f => f.PrintCount);
        var completedPrints = project.Files.Sum(f => f.PrintedCount);

        return new PrintProjectProgressDto(
            project.Id,
            project.Name,
            project.Status,
            project.Files.Count,
            project.Files.Count(f => f.PrintedCount >= f.PrintCount),
            totalPrints,
            completedPrints,
            totalPrints > 0 ? (int)Math.Round(100.0 * completedPrints / totalPrints) : 0,
            project.Files.OrderBy(f => f.SortOrder).Select(f => new FileProgressDto(
                f.Id,
                f.GcodeFile?.FileName ?? "Unknown",
                f.Status,
                f.PrintCount,
                f.PrintedCount,
                f.PrintedCount >= f.PrintCount)).ToList());
    }

    private async Task UpdateProjectStatusIfNeededAsync(PrintProject project, CancellationToken ct)
    {
        // Only auto-update if not manually set to Cancelled or OnHold
        if (project.Status is PrintProjectStatus.Cancelled or PrintProjectStatus.OnHold)
        {
            return;
        }

        var files = await db.PrintProjectFiles
            .Where(f => f.PrintProjectId == project.Id)
            .ToListAsync(ct);

        if (files.Count == 0)
        {
            return;
        }

        var allComplete = files.All(f => f.PrintedCount >= f.PrintCount);
        var anyInProgress = files.Any(f => f.PrintedCount > 0 && f.PrintedCount < f.PrintCount);

        if (allComplete)
        {
            project.Status = PrintProjectStatus.Completed;
            project.CompletedAt = DateTime.UtcNow;
        }
        else if (anyInProgress)
        {
            project.Status = PrintProjectStatus.InProgress;
        }
    }

    private PrintProjectDetailDto MapToDetailDto(PrintProject project)
    {
        return new PrintProjectDetailDto(
            project.Id,
            project.Name,
            project.Description,
            project.Status,
            project.Priority,
            project.DueDate,
            project.Notes,
            project.CreatedAt,
            project.UpdatedAt,
            project.CompletedAt,
            project.Files.OrderBy(f => f.SortOrder).Select(MapToFileDto).ToList());
    }

    private PrintProjectFileDto MapToFileDto(PrintProjectFile file)
    {
        return new PrintProjectFileDto(
            file.Id,
            file.GcodeFileId,
            file.GcodeFile?.Name ?? "Unknown",
            file.GcodeFile?.ThumbnailFileName is not null ? $"/api/gcode-files/thumbnail/{file.GcodeFileId}" : null,
            file.SpoolmanFilamentId,
            file.MaterialRequirement,
            file.PrintCount,
            file.PrintedCount,
            file.Status,
            file.SortOrder,
            file.Notes,
            file.LastPrintedAt,
            file.LastPrintJobId,
            file.GcodeFile?.EstimatedPrintTimeMinutes,
            file.GcodeFile?.EstimatedFilamentLengthMm,
            file.GcodeFile?.EstimatedFilamentWeightG,
            file.MaterialRequirement ?? file.GcodeFile?.RequiredMaterial,
            file.GcodeFile?.RequiredNozzleDiameter,
            file.GcodeFile?.ExtractedPrinterModelName,
            EstimatedCostPerCopy: null,
            file.PlateIndex,
            file.PlateName);
    }
}
