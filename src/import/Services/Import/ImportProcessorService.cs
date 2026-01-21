using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.UnitOfWork;
using FluentValidation.Results;

namespace Farm.Importing.Services.Import;

public class ImportProcessorService : IImportProcessorService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICatalogRepository _catalogRepo;
    private readonly FluentValidation.IValidator<CreatePrinterDto> _validator;

    public ImportProcessorService(IUnitOfWork unitOfWork, ICatalogRepository catalogRepo, FluentValidation.IValidator<CreatePrinterDto> validator)
    {
        _unitOfWork = unitOfWork;
        _catalogRepo = catalogRepo;
        _validator = validator;
    }

    public async Task<List<(string Name, string Status, Guid? Id, string? Reason)>> ProcessAsync(CreatePrinterDto[] dtos, string duplicateHandling, CancellationToken ct)
    {
        var results = new List<(string Name, string Status, Guid? Id, string? Reason)>();

        foreach (var dto in dtos)
        {
            try
            {
                ValidationResult validationResult = await _validator.ValidateAsync(dto, ct);
                if (!validationResult.IsValid)
                {
                    results.Add((dto.Name ?? string.Empty, "Failed", null, string.Join(';', validationResult.Errors.Select(e => e.ErrorMessage))));
                    continue;
                }

                // Check for duplicates using repository
                bool exists = await _unitOfWork.Printers.ExistsByNameOrServerUrlAsync(dto.Name ?? string.Empty, dto.ServerUrl ?? string.Empty, ct);
                if (exists)
                {
                    if (duplicateHandling.Equals("skip", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add((dto.Name ?? string.Empty, "Skipped", null, "Exists"));
                        continue;
                    }
                    else if (duplicateHandling.Equals("error", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add((dto.Name ?? string.Empty, "Failed", null, "Duplicate exists"));
                        continue;
                    }
                    else if (duplicateHandling.Equals("update", StringComparison.OrdinalIgnoreCase))
                    {
                        // Find existing printer to update - use tracked query for updates
                        var allPrinters = await _unitOfWork.Printers.GetAllForTemplateUpdateAsync(ct);
                        var existing = allPrinters.FirstOrDefault(p => p.Name == dto.Name || p.ServerUrl == dto.ServerUrl);
                        if (existing != null)
                        {
                            existing.Name = dto.Name ?? existing.Name;
                            existing.Notes = dto.Notes ?? existing.Notes;
                            existing.ApiKey = dto.ApiKey ?? existing.ApiKey;
                            existing.OriginalServerUrl = dto.OriginalServerUrl ?? existing.OriginalServerUrl;
                            existing.DateAcquired = dto.DateAcquired ?? existing.DateAcquired;
                            existing.Backend = (int)dto.Backend;
                            await _unitOfWork.SaveChangesAsync(ct);
                            results.Add((dto.Name ?? string.Empty, "Imported", existing.Id, "Updated"));
                            continue;
                        }
                    }
                }

                // Create new printer
                var created = await CreatePrinterFromDtoAsync(dto, ct);
                results.Add((created.Name, "Imported", created.Id, null));
            }
            catch (Exception ex)
            {
                results.Add((dto.Name ?? string.Empty, "Failed", null, ex.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// Strips the port from a server URL, returning only scheme + host.
    /// Used when persisting ServerUrl to the database (port is managed via FrontendPort field).
    /// Example: "http://192.168.1.50:8080/api" -> "http://192.168.1.50"
    /// </summary>
    private static string StripPortFromServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return string.Empty;
        }

        try
        {
            Uri uri = new Uri(serverUrl.Trim());
            UriBuilder ub = new UriBuilder(uri)
            {
                Port = -1,  // -1 means use default port (not explicitly shown in URI)
                Path = string.Empty,  // Remove any paths
                Query = string.Empty
            };
            return ub.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            // Fallback: just remove port manually if URI parsing fails
            string trimmed = serverUrl.Trim();
            if (trimmed.Contains("://"))
            {
                string[] parts = trimmed.Split(new[] { "://" }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    string hostPart = parts[1];

                    // Remove port and path
                    int colonIndex = hostPart.IndexOf(':');
                    int slashIndex = hostPart.IndexOf('/');

                    if (colonIndex > 0 || slashIndex > 0)
                    {
                        int endIndex = hostPart.Length;

                        if (colonIndex > 0)
                        {
                            endIndex = Math.Min(endIndex, colonIndex);
                        }

                        if (slashIndex > 0)
                        {
                            endIndex = Math.Min(endIndex, slashIndex);
                        }

                        hostPart = hostPart.Substring(0, endIndex);
                    }

                    return parts[0] + "://" + hostPart;
                }
            }

            return trimmed;
        }
    }

    private async Task<Farm.Infrastructure.PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct)
    {
        Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
        if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            var manufacturers = await _catalogRepo.GetManufacturersAsync(ct);
            var existing = manufacturers.FirstOrDefault(m => m.Name == name);
            if (existing.Name == null)
            {
                // Add new manufacturer
                manufacturerId = Guid.NewGuid();
                await _catalogRepo.AddManufacturerAsync(manufacturerId, name, null, null, ct);
            }
            else
            {
                manufacturerId = existing.Id;
            }
        }

        Guid modelId = dto.ModelId ?? Guid.Empty;
        if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            var models = await _catalogRepo.GetModelsCachedAsync(manufacturerId, ct);
            var existingModel = models.FirstOrDefault(m => m.ManufacturerId == manufacturerId && m.Name == mname);
            if (existingModel?.Id == null || existingModel.Id == Guid.Empty)
            {
                // Add new model
                var newModel = new Farm.Infrastructure.Domain.PrinterModel
                {
                    Id = Guid.NewGuid(),
                    ManufacturerId = manufacturerId,
                    Name = mname
                };
                await _catalogRepo.AddModelAsync(newModel, ct);
                modelId = newModel.Id;
            }
            else
            {
                modelId = existingModel.Id;
            }
        }

        if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
        {
            Guid? defMan = await _catalogRepo.GetUnknownManufacturerIdAsync(ct);
            Guid? defModel = await _catalogRepo.GetUnknownModelIdAsync(ct);

            if (manufacturerId == Guid.Empty && defMan.HasValue)
            {
                manufacturerId = defMan.Value;
            }

            if (modelId == Guid.Empty && defModel.HasValue)
            {
                modelId = defModel.Value;
            }
        }

        string normalizedInput = dto.ServerUrl ?? string.Empty;

        // Strip port from ServerUrl - port is managed via BackendPort field, not stored in ServerUrl
        string serverUrlWithoutPort = StripPortFromServerUrl(normalizedInput);

        var p = new Farm.Infrastructure.Domain.Printer
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ServerUrl = serverUrlWithoutPort,
            OriginalServerUrl = dto.OriginalServerUrl,
            IpAddress = null,
            Notes = dto.Notes,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            DateAcquired = dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey,

            // Populate hardware specs from DTO (populated from exported data or discovery)
            MaxBuildVolumeX = dto.MaxBuildVolumeX,
            MaxBuildVolumeY = dto.MaxBuildVolumeY,
            MaxBuildVolumeZ = dto.MaxBuildVolumeZ,
            HasHeatedBed = dto.HasHeatedBed,
            HasEnclosure = dto.HasEnclosure,
            MultiMaterial = dto.MultiMaterial,
            SupportsAutoLeveling = dto.SupportsAutoLeveling,
            MaxBedTemp = dto.MaxBedTemp,
            CurrentMaterial = dto.CurrentMaterial,
            CurrentSpoolId = dto.CurrentSpoolId,
            IsAvailable = true
        };

        await _unitOfWork.Printers.AddAsync(p, ct);

        // Create toolheads from import data or use default
        if (dto.Toolheads != null && dto.Toolheads.Count > 0)
        {
            // Import toolheads from DTO (exported data)
            foreach (var toolheadDto in dto.Toolheads.OrderBy(t => t.Index))
            {
                var toolhead = new Farm.Infrastructure.Domain.Toolhead
                {
                    Id = toolheadDto.Id ?? Guid.NewGuid(),
                    PrinterId = p.Id,
                    Name = toolheadDto.Name ?? $"Extruder {toolheadDto.Index + 1}",
                    Index = toolheadDto.Index,
                    HotendModelId = toolheadDto.HotendModelId,
                    ExtruderModelId = toolheadDto.ExtruderModelId,
                    ToolheadModelDefId = toolheadDto.ToolheadModelDefId,
                    NozzleModelId = toolheadDto.NozzleModelId,  // Nozzle diameter derived from nozzle model
                    SupportedMaterials = toolheadDto.SupportedMaterials ?? dto.SupportedMaterials,
                    IsPrimary = toolheadDto.IsPrimary,
                    UpdatedAt = DateTime.UtcNow
                };
                p.Toolheads.Add(toolhead);
            }
        }
        else
        {
            // Create default toolhead for the imported printer
            var defaultToolhead = new Farm.Infrastructure.Domain.Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = p.Id,
                Name = "Extruder 1",
                Index = 0,
                IsPrimary = true,

                // NozzleDiameter is now derived from NozzleModelId - use default nozzle model if needed
                SupportedMaterials = dto.SupportedMaterials,
                UpdatedAt = DateTime.UtcNow
            };
            p.Toolheads.Add(defaultToolhead);
        }

        await _unitOfWork.SaveChangesAsync(ct);

        return new Farm.Infrastructure.PrinterDto(
            Id: p.Id,
            Name: p.Name,
            Notes: p.Notes,
            IsOnline: false,
            State: null,
            ManufacturerName: null,
            ModelName: null,
            Progress: null,
            JobName: null,
            ThumbnailUrl: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            X: null,
            Y: null,
            Z: null,
            HotendTemp: null,
            BedTemp: null,
            HotendTarget: null,
            BedTarget: null,
            Backend: (PrinterBackend)p.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress,
            BackendPort: p.BackendPort,
            FrontendPort: p.FrontendPort,
            BackendUrl: p.BackendUrl,
            FrontendUrl: p.FrontendUrl);
    }
}
