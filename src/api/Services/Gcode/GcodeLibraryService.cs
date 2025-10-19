using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Repositories.Gcode;
using Farm.Web.Api.Repositories.Queue;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Gcode
{
    public class GcodeLibraryService : IGcodeLibraryService
    {
        private readonly IGcodeRepository _gcodeRepo;
        private readonly IQueueRepository _queueRepo;
        private readonly IUnifiedLoggingService _logger;

        public GcodeLibraryService(IGcodeRepository gcodeRepo, IQueueRepository queueRepo, IUnifiedLoggingService logger)
        {
            _gcodeRepo = gcodeRepo ?? throw new ArgumentNullException(nameof(gcodeRepo));
            _queueRepo = queueRepo ?? throw new ArgumentNullException(nameof(queueRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<GcodeFileDto>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? targetPrinterId, CancellationToken ct)
        {
            List<GcodeFile> files = await _gcodeRepo.QueryLibraryAsync(search, material, nozzleDiameter, targetPrinterId, ct);
            return files.Select(file => MapToDto(file)).ToArray();
        }

        public async Task<GcodeFileDto?> GetFileAsync(Guid id, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file is null)
            {
                return null;
            }

            return MapToDto(file);
        }

        public async Task<GcodeFileDto> UploadFileAsync(IFormFile file, CreateGcodeFileDto metadata, string webRootPath, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(metadata);

            // Compute hash
            string hash;
            using (Stream stream = file.OpenReadStream())
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hashBytes = await sha256.ComputeHashAsync(stream, ct);
                hash = Convert.ToHexString(hashBytes);
            }

            // Check duplicate
            GcodeFile? existing = await _gcodeRepo.FindByHashAsync(hash, ct);
            if (existing is not null)
            {
                throw new InvalidOperationException("duplicate");
            }

            // Ensure directory
            string libraryPath = Path.Combine(webRootPath, "gcode-library");
            string libraryRootFull = Path.GetFullPath(libraryPath);
            _ = Directory.CreateDirectory(libraryRootFull);

            // Save file
            string fileName = $"{Guid.NewGuid()}.gcode";
            string filePathFull = Path.GetFullPath(Path.Combine(libraryRootFull, fileName));
            if (!filePathFull.StartsWith(libraryRootFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid file path");
            }

            await using (FileStream fs = System.IO.File.Create(filePathFull))
            {
                await file.CopyToAsync(fs, ct);
            }

            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                OriginalFileName = file.FileName,
                DisplayName = string.IsNullOrEmpty(metadata.DisplayName) ? Path.GetFileNameWithoutExtension(file.FileName) : metadata.DisplayName,
                FilePath = filePathFull,
                FileSizeBytes = file.Length,
                FileHash = hash,
                UploadedAt = DateTime.UtcNow,
                Source = GcodeSource.Upload,
                Description = metadata.Description,
                Tags = metadata.Tags != null ? string.Join(',', metadata.Tags) : null,
                RequiredNozzleDiameter = metadata.RequiredNozzleDiameter,
                RequiredMaterial = metadata.RequiredMaterial,
                CompatibleMaterials = metadata.CompatibleMaterials,
                EstimatedPrintTimeMinutes = metadata.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm = metadata.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG = metadata.EstimatedFilamentWeightG,
                RequiredBuildVolumeX = metadata.RequiredBuildVolumeX,
                RequiredBuildVolumeY = metadata.RequiredBuildVolumeY,
                RequiredBuildVolumeZ = metadata.RequiredBuildVolumeZ,
                TargetPrinterId = metadata.TargetPrinterId,
                TargetModelId = metadata.TargetModelId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _gcodeRepo.AddAsync(gcodeFile, ct);
            await _gcodeRepo.SaveChangesAsync(ct);

            GcodeFile? saved = await _gcodeRepo.GetByIdWithIncludesAsync(gcodeFile.Id, ct);
            return MapToDto(saved!);
        }

        public async Task<GcodeFileDto> UpdateFileAsync(Guid id, UpdateGcodeFileDto request, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file == null)
            {
                throw new KeyNotFoundException();
            }

            if (!string.IsNullOrEmpty(request.DisplayName))
            {
                file.DisplayName = request.DisplayName;
            }

            if (request.Description != null)
            {
                file.Description = request.Description;
            }

            if (request.Tags != null)
            {
                file.Tags = string.Join(',', request.Tags);
            }

            if (request.RequiredNozzleDiameter.HasValue)
            {
                file.RequiredNozzleDiameter = request.RequiredNozzleDiameter;
            }

            if (!string.IsNullOrEmpty(request.RequiredMaterial))
            {
                file.RequiredMaterial = request.RequiredMaterial;
            }

            if (request.CompatibleMaterials != null)
            {
                file.CompatibleMaterials = request.CompatibleMaterials;
            }

            if (request.TargetPrinterId.HasValue)
            {
                file.TargetPrinterId = request.TargetPrinterId.Value;
            }

            if (request.TargetModelId.HasValue)
            {
                file.TargetModelId = request.TargetModelId.Value;
            }

            file.UpdatedAt = DateTime.UtcNow;

            await _gcodeRepo.SaveChangesAsync(ct);

            GcodeFile? saved = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            return MapToDto(saved!);
        }

        public async Task<bool> DeleteFileAsync(Guid id, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file == null)
            {
                return false;
            }

            int activeJobs = await _queueRepo.CountActiveJobsUsingGcodeAsync(id, ct);
            if (activeJobs > 0)
            {
                return false;
            }

            // Delete physical
            try
            {
                if (!string.IsNullOrEmpty(file.FilePath))
                {
                    if (System.IO.File.Exists(file.FilePath))
                    {
                        System.IO.File.Delete(file.FilePath);
                    }
                }
                if (!string.IsNullOrEmpty(file.ThumbnailPath) && System.IO.File.Exists(file.ThumbnailPath))
                {
                    System.IO.File.Delete(file.ThumbnailPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to delete physical file for gcode {id}");
            }

            await _gcodeRepo.RemoveAsync(file, ct);
            await _gcodeRepo.SaveChangesAsync(ct);
            return true;
        }

        public async Task<byte[]?> DownloadFileAsync(Guid id, string webRootPath, CancellationToken ct)
        {
            GcodeFile? file = await _gcodeRepo.GetByIdWithIncludesAsync(id, ct);
            if (file == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(file.FilePath))
            {
                return null;
            }

            if (!System.IO.File.Exists(file.FilePath))
            {
                return null;
            }

            return await System.IO.File.ReadAllBytesAsync(file.FilePath, ct);
        }

        private static GcodeFileDto MapToDto(GcodeFile file)
        {
            return new GcodeFileDto(
                Id: file.Id,
                OriginalFileName: file.OriginalFileName,
                DisplayName: file.DisplayName,
                FileSizeBytes: file.FileSizeBytes,
                UploadedAt: file.UploadedAt,
                Source: (GcodeSourceDto)(int)file.Source,
                SourcePrinterId: file.SourcePrinterId,
                SourcePrinterName: file.SourcePrinter?.Name,
                OriginalPrinterPath: file.OriginalPrinterPath,
                LastSeenOnPrinter: file.LastSeenOnPrinter,
                Description: file.Description,
                Tags: file.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                RequiredNozzleDiameter: file.RequiredNozzleDiameter,
                RequiredMaterial: file.RequiredMaterial,
                CompatibleMaterials: file.CompatibleMaterials,
                EstimatedPrintTimeMinutes: file.EstimatedPrintTimeMinutes,
                EstimatedFilamentLengthMm: file.EstimatedFilamentLengthMm,
                EstimatedFilamentWeightG: file.EstimatedFilamentWeightG,
                RequiredBuildVolumeX: file.RequiredBuildVolumeX,
                RequiredBuildVolumeY: file.RequiredBuildVolumeY,
                RequiredBuildVolumeZ: file.RequiredBuildVolumeZ,
                TargetPrinterId: file.TargetPrinterId,
                TargetPrinterName: file.TargetPrinter?.Name,
                TargetModelId: file.TargetModelId,
                TargetModelName: file.TargetModel?.Name,
                SlicerName: file.SlicerName,
                SlicerVersion: file.SlicerVersion,
                HasThumbnail: !string.IsNullOrEmpty(file.ThumbnailPath)
            );
        }
    }
}
