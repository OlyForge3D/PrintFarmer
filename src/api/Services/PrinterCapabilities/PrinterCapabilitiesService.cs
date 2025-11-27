using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterCapabilities;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.PrinterCapabilities
{
    public class PrinterCapabilitiesService : IPrinterCapabilitiesService
    {
        private readonly IPrinterCapabilitiesRepository _repo;
        private readonly IUnifiedLoggingService _logger;
        private readonly IPrinterCapabilityDiscoveryService _discovery;

        public PrinterCapabilitiesService(IPrinterCapabilitiesRepository repo, IUnifiedLoggingService logger, IPrinterCapabilityDiscoveryService discovery)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        }

        public async Task<IReadOnlyList<PrinterCapabilitiesDto>> GetAllAsync(CancellationToken ct = default)
        {
            List<Farm.Infrastructure.Domain.PrinterCapabilities> capabilities = await _repo.GetAllWithPrinterAsync(ct);

            return capabilities.Select(cap => new PrinterCapabilitiesDto(
                Id: cap.Id,
                PrinterId: cap.PrinterId,
                PrinterName: cap.Printer?.Name ?? string.Empty,
                NozzleDiameter: cap.NozzleDiameter,
                SupportedMaterials: cap.SupportedMaterials,
                MaxBuildVolumeX: cap.MaxBuildVolumeX,
                MaxBuildVolumeY: cap.MaxBuildVolumeY,
                MaxBuildVolumeZ: cap.MaxBuildVolumeZ,
                HasHeatedBed: cap.HasHeatedBed,
                HasEnclosure: cap.HasEnclosure,
                MultiMaterial: cap.MultiMaterial,
                SupportsAutoLeveling: cap.SupportsAutoLeveling,
                NumberOfExtruders: cap.NumberOfExtruders,
                MinHotendTemp: cap.MinHotendTemp,
                MaxHotendTemp: cap.MaxHotendTemp,
                MinBedTemp: cap.MinBedTemp,
                MaxBedTemp: cap.MaxBedTemp,
                CurrentMaterial: cap.CurrentMaterial,
                CurrentSpoolId: cap.CurrentSpoolId,
                IsAvailable: cap.IsAvailable,
                LastUpdated: cap.LastUpdated
            )).ToList();
        }

        public async Task<PrinterCapabilitiesDto?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct = default)
        {
            Farm.Infrastructure.Domain.PrinterCapabilities? cap = await _repo.GetByPrinterIdAsync(printerId, ct);

            if (cap == null)
            {
                return null;
            }

            return new PrinterCapabilitiesDto(
                Id: cap.Id,
                PrinterId: cap.PrinterId,
                PrinterName: cap.Printer?.Name ?? string.Empty,
                NozzleDiameter: cap.NozzleDiameter,
                SupportedMaterials: cap.SupportedMaterials,
                MaxBuildVolumeX: cap.MaxBuildVolumeX,
                MaxBuildVolumeY: cap.MaxBuildVolumeY,
                MaxBuildVolumeZ: cap.MaxBuildVolumeZ,
                HasHeatedBed: cap.HasHeatedBed,
                HasEnclosure: cap.HasEnclosure,
                MultiMaterial: cap.MultiMaterial,
                SupportsAutoLeveling: cap.SupportsAutoLeveling,
                NumberOfExtruders: cap.NumberOfExtruders,
                MinHotendTemp: cap.MinHotendTemp,
                MaxHotendTemp: cap.MaxHotendTemp,
                MinBedTemp: cap.MinBedTemp,
                MaxBedTemp: cap.MaxBedTemp,
                CurrentMaterial: cap.CurrentMaterial,
                CurrentSpoolId: cap.CurrentSpoolId,
                IsAvailable: cap.IsAvailable,
                LastUpdated: cap.LastUpdated
            );
        }

        public async Task<PrinterCapabilitiesDto?> CreateAsync(CreatePrinterCapabilitiesDto request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            Printer? printer = await _repo.FindPrinterAsync(request.PrinterId, ct);
            if (printer == null)
            {
                return null;
            }

            bool exists = await _repo.ExistsByPrinterIdAsync(request.PrinterId, ct);
            if (exists)
            {
                return null;
            }

            Farm.Infrastructure.Domain.PrinterCapabilities capabilities = new()
            {
                Id = Guid.NewGuid(),
                PrinterId = request.PrinterId,
                NozzleDiameter = request.NozzleDiameter,
                SupportedMaterials = request.SupportedMaterials,
                MaxBuildVolumeX = request.MaxBuildVolumeX,
                MaxBuildVolumeY = request.MaxBuildVolumeY,
                MaxBuildVolumeZ = request.MaxBuildVolumeZ,
                HasHeatedBed = request.HasHeatedBed,
                HasEnclosure = request.HasEnclosure,
                MultiMaterial = request.MultiMaterial,
                NumberOfExtruders = request.NumberOfExtruders,
                MinHotendTemp = request.MinHotendTemp,
                MaxHotendTemp = request.MaxHotendTemp,
                MinBedTemp = request.MinBedTemp,
                MaxBedTemp = request.MaxBedTemp,
                IsAvailable = true,
                LastUpdated = DateTime.UtcNow
            };

            if (!request.MaxBuildVolumeX.HasValue || !request.MaxBuildVolumeY.HasValue || !request.MaxBuildVolumeZ.HasValue ||
                !request.NozzleDiameter.HasValue || !request.MaxHotendTemp.HasValue)
            {
                try
                {
                    Farm.Infrastructure.Domain.PrinterCapabilities? defaults = await _discovery.GetModelDefaultCapabilitiesAsync(printer);
                    if (defaults != null)
                    {
                        capabilities.MaxBuildVolumeX ??= defaults.MaxBuildVolumeX;
                        capabilities.MaxBuildVolumeY ??= defaults.MaxBuildVolumeY;
                        capabilities.MaxBuildVolumeZ ??= defaults.MaxBuildVolumeZ;
                        capabilities.NozzleDiameter ??= defaults.NozzleDiameter;
                        capabilities.MaxHotendTemp ??= defaults.MaxHotendTemp;
                        capabilities.MaxBedTemp ??= defaults.MaxBedTemp;
                        capabilities.MinHotendTemp ??= defaults.MinHotendTemp;
                        capabilities.MinBedTemp ??= defaults.MinBedTemp;
                        capabilities.SupportedMaterials ??= defaults.SupportedMaterials;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to apply model defaults for printer {request.PrinterId}: {ex.Message}");
                }
            }

            await _repo.AddAsync(capabilities, ct);
            await _repo.SaveChangesAsync(ct);
            await _repo.LoadPrinterReferenceAsync(capabilities, ct);

            return new PrinterCapabilitiesDto(
                Id: capabilities.Id,
                PrinterId: capabilities.PrinterId,
                PrinterName: capabilities.Printer?.Name ?? string.Empty,
                NozzleDiameter: capabilities.NozzleDiameter,
                SupportedMaterials: capabilities.SupportedMaterials,
                MaxBuildVolumeX: capabilities.MaxBuildVolumeX,
                MaxBuildVolumeY: capabilities.MaxBuildVolumeY,
                MaxBuildVolumeZ: capabilities.MaxBuildVolumeZ,
                HasHeatedBed: capabilities.HasHeatedBed,
                HasEnclosure: capabilities.HasEnclosure,
                MultiMaterial: capabilities.MultiMaterial,
                SupportsAutoLeveling: capabilities.SupportsAutoLeveling,
                NumberOfExtruders: capabilities.NumberOfExtruders,
                MinHotendTemp: capabilities.MinHotendTemp,
                MaxHotendTemp: capabilities.MaxHotendTemp,
                MinBedTemp: capabilities.MinBedTemp,
                MaxBedTemp: capabilities.MaxBedTemp,
                CurrentMaterial: capabilities.CurrentMaterial,
                CurrentSpoolId: capabilities.CurrentSpoolId,
                IsAvailable: capabilities.IsAvailable,
                LastUpdated: capabilities.LastUpdated
            );
        }

        public async Task<PrinterCapabilitiesDto?> CreateOrUpdateAsync(Guid printerId, UpdatePrinterCapabilitiesDto request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            Printer? printer = await _repo.FindPrinterAsync(printerId, ct);
            if (printer == null)
            {
                return null;
            }

            Farm.Infrastructure.Domain.PrinterCapabilities? cap = await _repo.GetByPrinterIdAsync(printerId, ct);
            if (cap == null)
            {
                cap = new Farm.Infrastructure.Domain.PrinterCapabilities
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printerId,
                    NozzleDiameter = request.NozzleDiameter,
                    SupportedMaterials = request.SupportedMaterials,
                    MaxBuildVolumeX = request.MaxBuildVolumeX,
                    MaxBuildVolumeY = request.MaxBuildVolumeY,
                    MaxBuildVolumeZ = request.MaxBuildVolumeZ,
                    HasHeatedBed = request.HasHeatedBed,
                    HasEnclosure = request.HasEnclosure,
                    MultiMaterial = request.MultiMaterial,
                    NumberOfExtruders = request.NumberOfExtruders,
                    MinHotendTemp = request.MinHotendTemp,
                    MaxHotendTemp = request.MaxHotendTemp,
                    MinBedTemp = request.MinBedTemp,
                    MaxBedTemp = request.MaxBedTemp,
                    IsAvailable = true,
                    LastUpdated = DateTime.UtcNow
                };

                await _repo.AddAsync(cap, ct);
            }
            else
            {
                cap.NozzleDiameter = request.NozzleDiameter;
                cap.SupportedMaterials = request.SupportedMaterials;
                cap.MaxBuildVolumeX = request.MaxBuildVolumeX;
                cap.MaxBuildVolumeY = request.MaxBuildVolumeY;
                cap.MaxBuildVolumeZ = request.MaxBuildVolumeZ;
                cap.HasHeatedBed = request.HasHeatedBed;
                cap.HasEnclosure = request.HasEnclosure;
                cap.MultiMaterial = request.MultiMaterial;
                cap.NumberOfExtruders = request.NumberOfExtruders;
                cap.MinHotendTemp = request.MinHotendTemp;
                cap.MaxHotendTemp = request.MaxHotendTemp;
                cap.MinBedTemp = request.MinBedTemp;
                cap.MaxBedTemp = request.MaxBedTemp;
                cap.LastUpdated = DateTime.UtcNow;
            }

            await _repo.SaveChangesAsync(ct);
            await _repo.LoadPrinterReferenceAsync(cap, ct);

            return new PrinterCapabilitiesDto(
            Id: cap.Id,
            PrinterId: cap.PrinterId,
            PrinterName: cap.Printer?.Name ?? string.Empty,
            NozzleDiameter: cap.NozzleDiameter,
            SupportedMaterials: cap.SupportedMaterials,
            MaxBuildVolumeX: cap.MaxBuildVolumeX,
            MaxBuildVolumeY: cap.MaxBuildVolumeY,
            MaxBuildVolumeZ: cap.MaxBuildVolumeZ,
            HasHeatedBed: cap.HasHeatedBed,
            HasEnclosure: cap.HasEnclosure,
            MultiMaterial: cap.MultiMaterial,
            SupportsAutoLeveling: cap.SupportsAutoLeveling,
            NumberOfExtruders: cap.NumberOfExtruders,
            MinHotendTemp: cap.MinHotendTemp,
            MaxHotendTemp: cap.MaxHotendTemp,
            MinBedTemp: cap.MinBedTemp,
            MaxBedTemp: cap.MaxBedTemp,
            CurrentMaterial: cap.CurrentMaterial,
            CurrentSpoolId: cap.CurrentSpoolId,
            IsAvailable: cap.IsAvailable,
            LastUpdated: cap.LastUpdated
        );
        }

        public async Task<IReadOnlyList<PrinterDto>> GetCompatiblePrintersAsync(Guid gcodeFileId, CancellationToken ct = default)
        {
            GcodeFile? gcodeFile = await _repo.FindGcodeFileAsync(gcodeFileId, ct);
            if (gcodeFile == null)
            {
                return new List<PrinterDto>();
            }

            List<Farm.Infrastructure.Domain.PrinterCapabilities> allPrinters = await _repo.GetAvailableWithPrinterAsync(ct);

            List<PrinterDto> compatible = new List<PrinterDto>();
            foreach (Farm.Infrastructure.Domain.PrinterCapabilities cap in allPrinters)
            {
                bool isCompatible = true;
                if (gcodeFile.RequiredNozzleDiameter.HasValue && cap.NozzleDiameter.HasValue && Math.Abs(cap.NozzleDiameter.Value - gcodeFile.RequiredNozzleDiameter.Value) > 0.001)
                {
                    isCompatible = false;
                }

                if (!string.IsNullOrEmpty(gcodeFile.RequiredMaterial) && cap.SupportedMaterials != null && !cap.SupportedMaterials.Contains(gcodeFile.RequiredMaterial))
                {
                    isCompatible = false;
                }

                if (gcodeFile.RequiredBuildVolumeX.HasValue && cap.MaxBuildVolumeX.HasValue && gcodeFile.RequiredBuildVolumeX.Value > cap.MaxBuildVolumeX.Value)
                {
                    isCompatible = false;
                }

                if (gcodeFile.RequiredBuildVolumeY.HasValue && cap.MaxBuildVolumeY.HasValue && gcodeFile.RequiredBuildVolumeY.Value > cap.MaxBuildVolumeY.Value)
                {
                    isCompatible = false;
                }

                if (gcodeFile.RequiredBuildVolumeZ.HasValue && cap.MaxBuildVolumeZ.HasValue && gcodeFile.RequiredBuildVolumeZ.Value > cap.MaxBuildVolumeZ.Value)
                {
                    isCompatible = false;
                }

                if (isCompatible)
                {
                    compatible.Add(new PrinterDto(
                        Id: cap.Printer.Id,
                        Name: cap.Printer.Name,
                        ServerUrl: cap.Printer.ServerUrl,
                        Notes: cap.Printer.Notes,
                        IsOnline: false,
                        State: null,
                        ManufacturerName: cap.Printer.Manufacturer?.Name,
                        ModelName: cap.Printer.Model?.Name,
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
                        Backend: (PrinterBackend)cap.Printer.Backend,
                        ApiKey: cap.Printer.ApiKey,
                        OriginalServerUrl: cap.Printer.OriginalServerUrl,
                        IpAddress: cap.Printer.IpAddress,
                        SpoolInfo: null
                    ));
                }
            }

            return compatible;
        }

        public async Task<bool> DeleteAsync(Guid printerId, CancellationToken ct = default)
        {
            Farm.Infrastructure.Domain.PrinterCapabilities? cap = await _repo.GetByPrinterIdAsync(printerId, ct);
            if (cap == null)
            {
                return false;
            }

            await _repo.RemoveAsync(cap, ct);
            await _repo.SaveChangesAsync(ct);
            return true;
        }

        public async Task<(PrinterCapabilitiesDto? result, bool isNew)> DiscoverAsync(Guid printerId, CancellationToken ct = default)
        {
            Printer? printer = await _repo.GetPrinterWithModelAndManufacturerAsync(printerId, ct);
            if (printer == null)
            {
                return (null, false);
            }

            Farm.Infrastructure.Domain.PrinterCapabilities? existing = await _repo.GetByPrinterIdAsync(printerId, ct);
            Farm.Infrastructure.Domain.PrinterCapabilities capabilities;
            bool isNew = false;
            if (existing != null)
            {
                capabilities = await _discovery.RefreshCapabilitiesAsync(existing, printer, ct);
            }
            else
            {
                Farm.Infrastructure.Domain.PrinterCapabilities? discovered = await _discovery.DiscoverCapabilitiesAsync(printer, ct);
                if (discovered == null)
                {
                    return (null, false);
                }

                capabilities = discovered;
                await _repo.AddAsync(capabilities, ct);
                isNew = true;
            }

            await _repo.SaveChangesAsync(ct);
            await _repo.LoadPrinterReferenceAsync(capabilities, ct);

            PrinterCapabilitiesDto dto = new PrinterCapabilitiesDto(
                Id: capabilities.Id,
                PrinterId: capabilities.PrinterId,
                PrinterName: capabilities.Printer?.Name ?? string.Empty,
                NozzleDiameter: capabilities.NozzleDiameter,
                SupportedMaterials: capabilities.SupportedMaterials,
                MaxBuildVolumeX: capabilities.MaxBuildVolumeX,
                MaxBuildVolumeY: capabilities.MaxBuildVolumeY,
                MaxBuildVolumeZ: capabilities.MaxBuildVolumeZ,
                HasHeatedBed: capabilities.HasHeatedBed,
                HasEnclosure: capabilities.HasEnclosure,
                MultiMaterial: capabilities.MultiMaterial,
                NumberOfExtruders: capabilities.NumberOfExtruders,
                MinHotendTemp: capabilities.MinHotendTemp,
                MaxHotendTemp: capabilities.MaxHotendTemp,
                MinBedTemp: capabilities.MinBedTemp,
                MaxBedTemp: capabilities.MaxBedTemp,
                CurrentMaterial: capabilities.CurrentMaterial,
                CurrentSpoolId: capabilities.CurrentSpoolId,
                IsAvailable: capabilities.IsAvailable,
                LastUpdated: capabilities.LastUpdated
            );

            return (dto, isNew);
        }

        public async Task<CapabilityValidationResult> ValidateAsync(Guid printerId, CancellationToken ct = default)
        {
            Printer? printer = await _repo.GetPrinterWithModelAndManufacturerAsync(printerId, ct);
            if (printer == null)
            {
                return new CapabilityValidationResult { IsValid = false };
            }

            Farm.Infrastructure.Domain.PrinterCapabilities? cap = await _repo.GetByPrinterIdAsync(printerId, ct);
            if (cap == null)
            {
                return new CapabilityValidationResult { IsValid = false };
            }

            CapabilityValidationResult res = await _discovery.ValidateCapabilitiesAsync(cap, printer);
            return res;
        }

        public async Task<PrinterCapabilitiesDto?> GetModelDefaultsAsync(Guid printerId, CancellationToken ct = default)
        {
            Printer? printer = await _repo.GetPrinterWithModelAndManufacturerAsync(printerId, ct);
            if (printer == null)
            {
                return null;
            }

            Farm.Infrastructure.Domain.PrinterCapabilities? defaults = await _discovery.GetModelDefaultCapabilitiesAsync(printer);
            if (defaults == null)
            {
                return null;
            }

            return new PrinterCapabilitiesDto(
                Id: defaults.Id,
                PrinterId: defaults.PrinterId,
                PrinterName: printer.Name,
                NozzleDiameter: defaults.NozzleDiameter,
                SupportedMaterials: defaults.SupportedMaterials,
                MaxBuildVolumeX: defaults.MaxBuildVolumeX,
                MaxBuildVolumeY: defaults.MaxBuildVolumeY,
                MaxBuildVolumeZ: defaults.MaxBuildVolumeZ,
                HasHeatedBed: defaults.HasHeatedBed,
                HasEnclosure: defaults.HasEnclosure,
                MultiMaterial: defaults.MultiMaterial,
                NumberOfExtruders: defaults.NumberOfExtruders,
                MinHotendTemp: defaults.MinHotendTemp,
                MaxHotendTemp: defaults.MaxHotendTemp,
                MinBedTemp: defaults.MinBedTemp,
                MaxBedTemp: defaults.MaxBedTemp,
                CurrentMaterial: defaults.CurrentMaterial,
                CurrentSpoolId: defaults.CurrentSpoolId,
                IsAvailable: defaults.IsAvailable,
                LastUpdated: defaults.LastUpdated
            );
        }
    }
}
