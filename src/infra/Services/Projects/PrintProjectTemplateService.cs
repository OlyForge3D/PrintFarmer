using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Projects;
using Farm.Infrastructure.Services.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Projects;

/// <summary>
/// Service implementation for managing print project templates.
/// </summary>
public class PrintProjectTemplateService(AppDbContext db, ILogger<PrintProjectTemplateService> logger) : IPrintProjectTemplateService
{
    public async Task<IReadOnlyList<PrintProjectTemplateListDto>> GetTemplatesAsync(
        string? category = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var query = db.PrintProjectTemplates
            .Include(t => t.Files)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(t => t.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t => EF.Functions.Like(t.Name, $"%{search}%"));
        }

        var templates = await query
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        return templates.Select(t => new PrintProjectTemplateListDto(
            t.Id,
            t.Name,
            t.Description,
            t.Category,
            t.Files.Count,
            t.Files.Sum(f => f.PrintCount),
            t.IsSystemTemplate,
            t.SortOrder)).ToList();
    }

    public async Task<PrintProjectTemplateDetailDto?> GetTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await db.PrintProjectTemplates
            .Include(t => t.Files.OrderBy(f => f.SortOrder))
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);

        return template is null ? null : MapToDetailDto(template);
    }

    public async Task<PrintProjectTemplateDetailDto> CreateTemplateAsync(CreatePrintProjectTemplateRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var maxSortOrder = await db.PrintProjectTemplates.MaxAsync(t => (int?)t.SortOrder, ct) ?? -1;

        var template = new PrintProjectTemplate
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            DefaultPriority = request.DefaultPriority,
            DefaultNotes = request.DefaultNotes,
            IsSystemTemplate = false,
            SortOrder = maxSortOrder + 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.PrintProjectTemplates.Add(template);

        if (request.Files is { Count: > 0 })
        {
            var sortOrder = 0;
            foreach (var fileRequest in request.Files)
            {
                var templateFile = new PrintProjectTemplateFile
                {
                    Id = Guid.NewGuid(),
                    PrintProjectTemplateId = template.Id,
                    Name = fileRequest.Name,
                    FileNamePattern = fileRequest.FileNamePattern,
                    ColorRequirement = fileRequest.ColorRequirement,
                    MaterialRequirement = fileRequest.MaterialRequirement,
                    PrintCount = Math.Max(1, fileRequest.PrintCount),
                    SortOrder = sortOrder++,
                    Notes = fileRequest.Notes
                };
                db.PrintProjectTemplateFiles.Add(templateFile);
                template.Files.Add(templateFile);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation($"Created project template {template.Id}: {template.Name}");

        return MapToDetailDto(template);
    }

    public async Task<PrintProjectTemplateDetailDto?> UpdateTemplateAsync(Guid templateId, UpdatePrintProjectTemplateRequest request, CancellationToken ct = default)
    {
        var template = await db.PrintProjectTemplates
            .Include(t => t.Files.OrderBy(f => f.SortOrder))
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);

        if (template is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        if (request.Name is not null)
        {
            template.Name = request.Name;
        }

        if (request.Description is not null)
        {
            template.Description = request.Description;
        }

        if (request.Category is not null)
        {
            template.Category = request.Category;
        }

        if (request.DefaultPriority.HasValue)
        {
            template.DefaultPriority = request.DefaultPriority.Value;
        }

        if (request.DefaultNotes is not null)
        {
            template.DefaultNotes = request.DefaultNotes;
        }

        if (request.SortOrder.HasValue)
        {
            template.SortOrder = request.SortOrder.Value;
        }

        template.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return MapToDetailDto(template);
    }

    public async Task<bool> DeleteTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await db.PrintProjectTemplates.FindAsync([templateId], ct);

        if (template is null)
        {
            return false;
        }

        if (template.IsSystemTemplate)
        {
            logger.LogWarning($"Cannot delete system template {templateId}");
            return false;
        }

        db.PrintProjectTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PrintProjectTemplateFileDto?> AddFileToTemplateAsync(Guid templateId, CreateTemplateFileRequest request, CancellationToken ct = default)
    {
        var template = await db.PrintProjectTemplates
            .Include(t => t.Files)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);

        if (template is null)
        {
            return null;
        }

        var maxSortOrder = template.Files.Count > 0 ? template.Files.Max(f => f.SortOrder) : -1;

        var templateFile = new PrintProjectTemplateFile
        {
            Id = Guid.NewGuid(),
            PrintProjectTemplateId = templateId,
            Name = request.Name,
            FileNamePattern = request.FileNamePattern,
            ColorRequirement = request.ColorRequirement,
            MaterialRequirement = request.MaterialRequirement,
            PrintCount = Math.Max(1, request.PrintCount),
            SortOrder = maxSortOrder + 1,
            Notes = request.Notes
        };

        db.PrintProjectTemplateFiles.Add(templateFile);
        template.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return MapToFileDto(templateFile);
    }

    public async Task<bool> RemoveFileFromTemplateAsync(Guid templateId, Guid fileId, CancellationToken ct = default)
    {
        var templateFile = await db.PrintProjectTemplateFiles
            .FirstOrDefaultAsync(f => f.PrintProjectTemplateId == templateId && f.Id == fileId, ct);

        if (templateFile is null)
        {
            return false;
        }

        db.PrintProjectTemplateFiles.Remove(templateFile);

        var template = await db.PrintProjectTemplates.FindAsync([templateId], ct);
        if (template is not null)
        {
            template.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PrintProjectTemplateFileDto?> UpdateTemplateFileAsync(Guid templateId, Guid fileId, UpdateTemplateFileRequest request, CancellationToken ct = default)
    {
        var templateFile = await db.PrintProjectTemplateFiles
            .FirstOrDefaultAsync(f => f.PrintProjectTemplateId == templateId && f.Id == fileId, ct);

        if (templateFile is null)
        {
            return null;
        }

        if (request.Name is not null)
        {
            templateFile.Name = request.Name;
        }

        if (request.FileNamePattern is not null)
        {
            templateFile.FileNamePattern = request.FileNamePattern;
        }

        if (request.ColorRequirement.HasValue)
        {
            templateFile.ColorRequirement = request.ColorRequirement.Value;
        }

        if (request.MaterialRequirement is not null)
        {
            templateFile.MaterialRequirement = request.MaterialRequirement;
        }

        if (request.PrintCount.HasValue)
        {
            templateFile.PrintCount = Math.Max(1, request.PrintCount.Value);
        }

        if (request.SortOrder.HasValue)
        {
            templateFile.SortOrder = request.SortOrder.Value;
        }

        if (request.Notes is not null)
        {
            templateFile.Notes = request.Notes;
        }

        var template = await db.PrintProjectTemplates.FindAsync([templateId], ct);
        if (template is not null)
        {
            template.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return MapToFileDto(templateFile);
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        return await db.PrintProjectTemplates
            .Where(t => t.Category != null)
            .Select(t => t.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);
    }

    private static PrintProjectTemplateDetailDto MapToDetailDto(PrintProjectTemplate template)
    {
        return new PrintProjectTemplateDetailDto(
            template.Id,
            template.Name,
            template.Description,
            template.Category,
            template.DefaultPriority,
            template.DefaultNotes,
            template.IsSystemTemplate,
            template.SortOrder,
            template.Files.OrderBy(f => f.SortOrder).Select(MapToFileDto).ToList(),
            template.CreatedAt,
            template.UpdatedAt);
    }

    private static PrintProjectTemplateFileDto MapToFileDto(PrintProjectTemplateFile file)
    {
        return new PrintProjectTemplateFileDto(
            file.Id,
            file.Name,
            file.FileNamePattern,
            file.ColorRequirement,
            file.MaterialRequirement,
            file.PrintCount,
            file.SortOrder,
            file.Notes);
    }
}
