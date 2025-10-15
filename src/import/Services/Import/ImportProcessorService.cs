using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Importing.Services.Adapters;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace Farm.Importing.Services.Import;

public class ImportProcessorService : IImportProcessorService
{
    private readonly AppDbContext _db;
    private readonly FluentValidation.IValidator<CreatePrinterDto> _validator;
    private readonly IPrinterCapabilityDiscoveryAdapter _capabilityDiscovery;
    private readonly IDefaultCatalogAdapter _defaultCatalog;

    public ImportProcessorService(AppDbContext db, FluentValidation.IValidator<CreatePrinterDto> validator, IPrinterCapabilityDiscoveryAdapter capabilityDiscovery, IDefaultCatalogAdapter defaultCatalog)
    {
        _db = db;
        _validator = validator;
        _capabilityDiscovery = capabilityDiscovery;
        _defaultCatalog = defaultCatalog;
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

                // Existence
                Printer? existing = await _db.Printers.FirstOrDefaultAsync(p => p.Name == dto.Name || p.ServerUrl == dto.ServerUrl, ct);
                if (existing != null)
                {
                    if (duplicateHandling.Equals("skip", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add((dto.Name ?? string.Empty, "Skipped", existing.Id, "Exists"));
                        continue;
                    }
                    else if (duplicateHandling.Equals("error", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add((dto.Name ?? string.Empty, "Failed", null, "Duplicate exists"));
                        continue;
                    }
                    else if (duplicateHandling.Equals("update", StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Name = dto.Name ?? existing.Name;
                        existing.Notes = dto.Notes ?? existing.Notes;
                        existing.ApiKey = dto.ApiKey ?? existing.ApiKey;
                        existing.OriginalServerUrl = dto.OriginalServerUrl ?? existing.OriginalServerUrl;
                        existing.DateAcquired = dto.DateAcquired ?? existing.DateAcquired;
                        existing.Backend = (int)dto.Backend;
                        _ = _db.Printers.Update(existing);
                        await _db.SaveChangesAsync(ct);
                        results.Add((dto.Name ?? string.Empty, "Imported", existing.Id, "Updated"));
                        continue;
                    }
                }

                // Create
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

    private async Task<Farm.Web.Shared.PrinterDto> CreatePrinterFromDtoAsync(CreatePrinterDto dto, CancellationToken ct)
    {
        Guid manufacturerId = dto.ManufacturerId ?? Guid.Empty;
        if (manufacturerId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewManufacturerName))
        {
            string name = dto.NewManufacturerName!.Trim();
            var existing = await _db.Manufacturers.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new Farm.Infrastructure.Domain.Manufacturer { Id = Guid.NewGuid(), Name = name };
                _ = _db.Manufacturers.Add(existing);
                await _db.SaveChangesAsync(ct);
            }
            manufacturerId = existing.Id;
        }

        Guid modelId = dto.ModelId ?? Guid.Empty;
        if (modelId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.NewModelName) && manufacturerId != Guid.Empty)
        {
            string mname = dto.NewModelName!.Trim();
            var existingModel = await _db.Models.FirstOrDefaultAsync(m => m.ManufacturerId == manufacturerId && m.Name == mname, ct);
            if (existingModel is null)
            {
                existingModel = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturerId, Name = mname };
                _ = _db.Models.Add(existingModel);
                await _db.SaveChangesAsync(ct);
            }
            modelId = existingModel.Id;
        }

        if (manufacturerId == Guid.Empty || modelId == Guid.Empty)
        {
            (Guid defMan, Guid defModel) = await _defaultCatalog.GetDefaultCatalogIdsAsync();
            if (manufacturerId == Guid.Empty)
            {
                manufacturerId = defMan;
            }

            if (modelId == Guid.Empty)
            {
                modelId = defModel;
            }
        }

        int defaultPort = dto.Backend == Farm.Web.Shared.PrinterBackend.PrusaLink ? 80 : 7125;
        string normalizedInput = dto.ServerUrl ?? string.Empty;

        var p = new Farm.Infrastructure.Domain.Printer
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ServerUrl = normalizedInput,
            OriginalServerUrl = dto.OriginalServerUrl,
            IpAddress = null,
            Notes = dto.Notes,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            DateAcquired = dto.DateAcquired,
            Backend = (int)dto.Backend,
            ApiKey = dto.ApiKey
        };
        _ = _db.Printers.Add(p);
        await _db.SaveChangesAsync(ct);

        try
        {
            var printerForDisc = await _db.Printers.Include(pr => pr.Manufacturer).Include(pr => pr.Model).FirstOrDefaultAsync(pr => pr.Id == p.Id, ct);
            if (printerForDisc != null)
            {
                _ = await _capabilityDiscovery.DiscoverCapabilitiesAsync(printerForDisc, ct);
            }
        }
        catch { }

        return new Farm.Web.Shared.PrinterDto(
            Id: p.Id,
            Name: p.Name,
            ServerUrl: p.ServerUrl,
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
            Backend: (Farm.Web.Shared.PrinterBackend)p.Backend,
            ApiKey: p.ApiKey,
            OriginalServerUrl: p.OriginalServerUrl,
            IpAddress: p.IpAddress
        );
    }
}
