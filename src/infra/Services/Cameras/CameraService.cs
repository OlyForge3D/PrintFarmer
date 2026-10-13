using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Service for managing standalone cameras and aggregating all camera feeds.
/// </summary>
public class CameraService : ICameraService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly AppDbContext _db;
    private readonly ILogger<CameraService> _logger;
    private readonly IPrintersService _printersService;
    private readonly IStoragePathService _storagePathService;
    private readonly IHubContext<PrinterHub>? _hubContext;

    public CameraService(
        IUnitOfWork unitOfWork,
        AppDbContext db,
        ILogger<CameraService> logger,
        IPrintersService printersService,
        IStoragePathService storagePathService,
        IHubContext<PrinterHub>? hubContext = null)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(printersService);
        ArgumentNullException.ThrowIfNull(storagePathService);

        _unitOfWork = unitOfWork;
        _db = db;
        _logger = logger;
        _printersService = printersService;
        _storagePathService = storagePathService;
        _hubContext = hubContext;
    }

    private static CameraDto ToDto(Camera camera) => CameraDtoMapper.ToDto(camera);

    /// <inheritdoc />
    public async Task<List<Camera>> GetAllAsync(CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.Cameras.GetAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cameras");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<Camera>> GetEnabledAsync(CancellationToken ct)
    {
        try
        {
            return await _unitOfWork.Cameras.GetEnabledAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving enabled cameras");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Camera?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            return await _unitOfWork.Cameras.FindByIdAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding camera with ID {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Camera?> FindByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Camera name cannot be empty", nameof(name));
        }

        try
        {
            return await _unitOfWork.Cameras.FindByNameAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding camera with name {Name}", LogSanitizer.Sanitize(name));
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            return await _unitOfWork.Cameras.ExistsByNameAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking camera existence by name {Name}", LogSanitizer.Sanitize(name));
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto> CreateAsync(CreateCameraDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Camera name is required", nameof(dto));
        }

        try
        {
            // Check for duplicate name
            if (await ExistsByNameAsync(dto.Name, ct))
            {
                throw new InvalidOperationException($"A camera with name '{dto.Name}' already exists");
            }

            Printer? printer = null;
            if (dto.PrinterIds is null && dto.PrinterId.HasValue)
            {
                printer = await _printersService.FindByIdAsync(dto.PrinterId.Value, ct);
                if (printer == null)
                {
                    throw new InvalidOperationException($"Printer with ID '{dto.PrinterId.Value}' not found");
                }
            }

            Camera camera = new()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                StreamUrl = CameraContractClassifier.NormalizeTarget(dto.StreamUrl),
                SnapshotUrl = CameraContractClassifier.NormalizeTarget(dto.SnapshotUrl),
                IsEnabled = dto.IsEnabled,
                SortOrder = dto.SortOrder,
                Location = dto.Location?.Trim(),
                Source = dto.Source ?? CameraSource.Standalone,
                CameraType = dto.CameraType ?? CameraType.General,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (dto.PrinterIds is not null)
            {
                await ApplyPrinterLinksAsync(camera, dto.PrinterIds, dto.IsPrimary == true, preservedPrinterIds: null, ct);
            }
            else if (printer is not null)
            {
                bool hasPrimary = await _unitOfWork.Cameras.HasPrimaryCameraAsync(printer.Id, ct);
                camera.LinkToPrinter(printer.Id, isPrimary: dto.IsPrimary == true || !hasPrimary).Printer = printer;
                await ClearDismissalAsync(camera, printer.Id, ct);
                if (dto.IsPrimary == true)
                {
                    await DemoteOtherPrimariesAsync(printer.Id, camera.Id, ct);
                }
            }

            await ApplyLocationRuleAsync(camera, dto.LocationId, clearLocation: false, previousEffectiveLocationId: null, ct);

            _unitOfWork.Cameras.Add(camera);
            await _unitOfWork.SaveChangesAsync(ct);
            await NotifyCamerasUpdatedAsync(camera.PrinterLinks.Select(l => l.PrinterId), ct);

            _logger.LogInformation("Created camera {CameraName} with ID {CameraId}", LogSanitizer.Sanitize(camera.Name), camera.Id);

            return ToDto(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error creating camera {DtoName}", LogSanitizer.Sanitize(dto.Name));
            throw;
        }
    }

    /// <inheritdoc />
    public Task<CameraDto?> UpdateAsync(Guid id, UpdateCameraDto dto, CancellationToken ct) =>
        UpdateAsync(id, dto, [], ct);

    /// <inheritdoc />
    public async Task<CameraDto?> UpdateAsync(Guid id, UpdateCameraDto dto, IReadOnlyCollection<Guid> preservedPrinterIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(preservedPrinterIds);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            Camera? camera = await FindByIdAsync(id, ct);
            if (camera == null)
            {
                return null;
            }

            // Check for duplicate name if name is being changed
            if (!string.IsNullOrWhiteSpace(dto.Name) &&
                !dto.Name.Equals(camera.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (await ExistsByNameAsync(dto.Name, ct))
                {
                    throw new InvalidOperationException($"A camera with name '{dto.Name}' already exists");
                }

                camera.Name = dto.Name.Trim();
            }

            if (dto.Description != null)
            {
                camera.Description = dto.Description.Trim();
            }

            if (dto.StreamUrl != null)
            {
                camera.StreamUrl = string.IsNullOrWhiteSpace(dto.StreamUrl) ? null : dto.StreamUrl.Trim();
            }

            if (dto.SnapshotUrl != null)
            {
                camera.SnapshotUrl = string.IsNullOrWhiteSpace(dto.SnapshotUrl) ? null : dto.SnapshotUrl.Trim();
            }

            if (dto.IsEnabled.HasValue)
            {
                camera.IsEnabled = dto.IsEnabled.Value;
            }

            if (dto.SortOrder.HasValue)
            {
                camera.SortOrder = dto.SortOrder.Value;
            }

            if (dto.Location != null)
            {
                camera.Location = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim();
            }

            if (dto.Source.HasValue)
            {
                camera.Source = dto.Source.Value;
            }

            if (dto.CameraType.HasValue)
            {
                camera.CameraType = dto.CameraType.Value;
            }

            Guid? previousEffectiveLocationId = camera.GetEffectiveLocationId();
            List<Guid> printersBefore = camera.PrinterLinks.Select(l => l.PrinterId).ToList();
            if (dto.PrinterIds is not null)
            {
                await ApplyPrinterLinksAsync(camera, dto.PrinterIds, dto.IsPrimary == true, preservedPrinterIds, ct);
            }
            else if (dto.PrinterId.HasValue && dto.PrinterId != camera.GetPrimaryPrinterId())
            {
                Printer? printer = await _printersService.FindByIdAsync(dto.PrinterId.Value, ct);
                if (printer == null)
                {
                    throw new InvalidOperationException($"Printer with ID '{dto.PrinterId.Value}' not found");
                }

                // Legacy single-printer reassignment: replace all existing (visible) links with the new printer.
                await ApplyPrinterLinksAsync(camera, [printer.Id], dto.IsPrimary == true, preservedPrinterIds, ct);
            }

            await ApplyLocationRuleAsync(camera, dto.LocationId, dto.ClearLocationId == true, previousEffectiveLocationId, ct);

            MarkUserModified(camera);
            camera.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct);
            await NotifyCamerasUpdatedAsync(printersBefore.Concat(camera.PrinterLinks.Select(l => l.PrinterId)), ct);

            _logger.LogInformation("Updated camera {CameraName} with ID {CameraId}", LogSanitizer.Sanitize(camera.Name), camera.Id);

            return ToDto(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error updating camera {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            Camera? camera = await FindByIdAsync(id, ct);
            if (camera == null)
            {
                return false;
            }

            // Pre-delete snapshot files and DB rows before removing the camera entity.
            // The Camera→CameraSnapshot FK is Restrict, so EF Core will throw a FK
            // violation if any snapshot rows still reference this camera when it is removed.
            await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(id, _db, _storagePathService, _logger, ct);

            foreach (CameraPrinter link in camera.PrinterLinks.Where(l => l.IsPrimary).ToList())
            {
                await PromoteNextPrimaryAsync(link.PrinterId, camera.Id, ct);
            }

            foreach (CameraPrinter link in camera.PrinterLinks)
            {
                await DismissDiscoveredCameraAsync(camera, link.PrinterId, ct);
            }

            List<Guid> affectedPrinters = camera.PrinterLinks.Select(l => l.PrinterId).ToList();
            _unitOfWork.Cameras.Remove(camera);
            await _unitOfWork.SaveChangesAsync(ct);
            await NotifyCamerasUpdatedAsync(affectedPrinters, ct);

            _logger.LogInformation("Deleted camera {CameraName} with ID {CameraId}", LogSanitizer.Sanitize(camera.Name), camera.Id);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting camera {Id}", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto?> ToggleEnabledAsync(Guid id, bool isEnabled, CancellationToken ct)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Camera ID cannot be empty", nameof(id));
        }

        try
        {
            Camera? camera = await FindByIdAsync(id, ct);
            if (camera == null)
            {
                return null;
            }

            camera.IsEnabled = isEnabled;
            MarkUserModified(camera);
            camera.UpdatedAt = DateTime.UtcNow;

            await _unitOfWork.SaveChangesAsync(ct);
            await NotifyCamerasUpdatedAsync(camera.PrinterLinks.Select(l => l.PrinterId), ct);

            _logger.LogInformation("Toggled camera {CameraName} enabled status to {IsEnabled}", LogSanitizer.Sanitize(camera.Name), isEnabled);

            return ToDto(camera);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling camera {Id} enabled status", id);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto[]> GetAllDtosAsync(CancellationToken ct)
    {
        try
        {
            List<Camera> cameras = await GetAllAsync(ct);
            return cameras.Select(ToDto).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all camera DTOs");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto[]> GetEnabledCamerasAsync(CancellationToken ct)
    {
        try
        {
            List<Camera> cameras = await GetEnabledAsync(ct);
            return cameras.Select(ToDto).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting enabled camera DTOs");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<CameraDto>> GetByPrinterIdAsync(Guid printerId, CancellationToken ct)
    {
        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        try
        {
            List<Camera> cameras = await _unitOfWork.Cameras.GetByPrinterIdAsync(printerId, ct);
            return cameras.Select(ToDto).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cameras for printer {PrinterId}", printerId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CameraDto> CreateForPrinterAsync(Guid printerId, CreateCameraDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (printerId == Guid.Empty)
        {
            throw new ArgumentException("Printer ID cannot be empty", nameof(printerId));
        }

        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            throw new ArgumentException("Camera name is required", nameof(dto));
        }

        try
        {
            Printer? printer = await _printersService.FindByIdAsync(printerId, ct);
            if (printer == null)
            {
                throw new InvalidOperationException($"Printer with ID '{printerId}' not found");
            }

            // Check for duplicate name
            if (await ExistsByNameAsync(dto.Name, ct))
            {
                throw new InvalidOperationException($"A camera with name '{dto.Name}' already exists");
            }

            Camera camera = new()
            {
                Id = Guid.NewGuid(),
                Name = dto.Name.Trim(),
                Description = dto.Description?.Trim(),
                StreamUrl = CameraContractClassifier.NormalizeTarget(dto.StreamUrl),
                SnapshotUrl = CameraContractClassifier.NormalizeTarget(dto.SnapshotUrl),
                IsEnabled = dto.IsEnabled,
                SortOrder = dto.SortOrder,
                Location = dto.Location?.Trim(),
                Source = dto.Source ?? CameraSource.Standalone,
                CameraType = dto.CameraType ?? CameraType.General,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            bool hasPrimaryCamera = await _unitOfWork.Cameras.HasPrimaryCameraAsync(printerId, ct);
            camera.LinkToPrinter(printerId, isPrimary: !hasPrimaryCamera).Printer = printer;
            await ClearDismissalAsync(camera, printerId, ct);

            _unitOfWork.Cameras.Add(camera);
            await _unitOfWork.SaveChangesAsync(ct);

            _logger.LogInformation("Created camera {CameraName} with ID {CameraId} for printer {PrinterId}", LogSanitizer.Sanitize(camera.Name), camera.Id, printerId);

            return ToDto(camera);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not ArgumentException)
        {
            _logger.LogError(ex, "Error creating camera {DtoName} for printer {PrinterId}", LogSanitizer.Sanitize(dto.Name), printerId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<DisplayCameraDto>> GetDisplayCamerasAsync(CancellationToken ct)
    {
        try
        {
            List<Camera> cameras = await _unitOfWork.Cameras.GetEnabledWithPrinterAsync(ct);
            return cameras.Select(CameraDtoMapper.ToDisplayDto).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting display cameras");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<CameraDto>> QueryDtosAsync(Guid? printerId, Guid? locationId, bool enabledOnly, CancellationToken ct)
    {
        List<Camera> cameras = await _unitOfWork.Cameras.QueryAsync(printerId, locationId, enabledOnly, ct);
        return cameras.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<List<DisplayCameraDto>> QueryDisplayCamerasAsync(Guid? printerId, Guid? locationId, CancellationToken ct)
    {
        List<Camera> cameras = await _unitOfWork.Cameras.QueryAsync(printerId, locationId, enabledOnly: true, ct);
        return cameras.Select(CameraDtoMapper.ToDisplayDto).ToList();
    }

    /// <inheritdoc />
    public async Task<CameraDto?> LinkToPrinterAsync(Guid cameraId, Guid printerId, bool? isPrimary, CancellationToken ct)
    {
        Camera? camera = await FindByIdAsync(cameraId, ct);
        if (camera is null)
        {
            return null;
        }

        Printer printer = await _db.Printers.Include(p => p.Location).FirstOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw new ArgumentException($"Printer with ID '{printerId}' not found", nameof(printerId));

        Guid? previousEffectiveLocationId = camera.GetEffectiveLocationId();
        CameraPrinter? existing = camera.PrinterLinks.FirstOrDefault(l => l.PrinterId == printerId);
        if (existing is null)
        {
            bool makePrimary = isPrimary == true || !await _unitOfWork.Cameras.HasPrimaryCameraAsync(printerId, ct);
            int nextSortOrder = camera.PrinterLinks.Count == 0 ? 0 : camera.PrinterLinks.Max(l => l.SortOrder) + 1;
            camera.LinkToPrinter(printerId, makePrimary, nextSortOrder).Printer = printer;
            await ClearDismissalAsync(camera, printerId, ct);
            if (makePrimary)
            {
                await DemoteOtherPrimariesAsync(printerId, camera.Id, ct);
            }
        }
        else if (isPrimary == true && !existing.IsPrimary)
        {
            existing.IsPrimary = true;
            await DemoteOtherPrimariesAsync(printerId, camera.Id, ct);
        }
        else if (isPrimary == false && existing.IsPrimary && await PromoteNextPrimaryAsync(printerId, camera.Id, ct))
        {
            // Demoting is only honoured when another camera can take over as the printer's primary.
            existing.IsPrimary = false;
        }

        await ApplyLocationRuleAsync(camera, null, clearLocation: false, previousEffectiveLocationId, ct);
        MarkUserModified(camera);
        camera.UpdatedAt = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync(ct);
        await NotifyCamerasUpdatedAsync([printerId], ct);

        _logger.LogInformation("Linked camera {CameraId} to printer {PrinterId}", camera.Id, printerId);
        return ToDto(camera);
    }

    /// <inheritdoc />
    public async Task<CameraPrinterRemovalResult> UnlinkFromPrinterAsync(Guid cameraId, Guid printerId, CancellationToken ct)
    {
        Camera? camera = await FindByIdAsync(cameraId, ct);
        if (camera is null)
        {
            return CameraPrinterRemovalResult.CameraNotFound;
        }

        if (!await UnlinkCoreAsync(camera, printerId, ct))
        {
            return CameraPrinterRemovalResult.NotLinked;
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await NotifyCamerasUpdatedAsync([printerId], ct);
        _logger.LogInformation("Unlinked camera {CameraId} from printer {PrinterId}", camera.Id, printerId);
        return CameraPrinterRemovalResult.Unlinked;
    }

    /// <inheritdoc />
    public async Task<CameraPrinterRemovalResult> RemoveFromPrinterAsync(Guid printerId, Guid cameraId, CancellationToken ct)
    {
        Camera? camera = await FindByIdAsync(cameraId, ct);
        if (camera is null)
        {
            return CameraPrinterRemovalResult.CameraNotFound;
        }

        if (!await UnlinkCoreAsync(camera, printerId, ct))
        {
            return CameraPrinterRemovalResult.NotLinked;
        }

        // Auto-discovered cameras exist only because of a printer; once no printer references them they
        // are deleted. User-created (standalone-source) cameras are kept as unlinked cameras.
        if (camera.Source != CameraSource.Standalone && camera.PrinterLinks.Count == 0)
        {
            await SnapshotCleanupHelper.DeleteSnapshotsForCameraAsync(camera.Id, _db, _storagePathService, _logger, ct);
            _unitOfWork.Cameras.Remove(camera);
            await _unitOfWork.SaveChangesAsync(ct);
            await NotifyCamerasUpdatedAsync([printerId], ct);
            _logger.LogInformation("Deleted auto-discovered camera {CameraId} after removing it from printer {PrinterId}", camera.Id, printerId);
            return CameraPrinterRemovalResult.Deleted;
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await NotifyCamerasUpdatedAsync([printerId], ct);
        _logger.LogInformation("Removed camera {CameraId} from printer {PrinterId}", camera.Id, printerId);
        return CameraPrinterRemovalResult.Unlinked;
    }

    private async Task<bool> UnlinkCoreAsync(Camera camera, Guid printerId, CancellationToken ct)
    {
        CameraPrinter? link = camera.PrinterLinks.FirstOrDefault(l => l.PrinterId == printerId);
        if (link is null)
        {
            return false;
        }

        Guid? previousEffectiveLocationId = camera.GetEffectiveLocationId();
        await RemoveLinkAsync(camera, link, ct);
        await ApplyLocationRuleAsync(camera, null, clearLocation: false, previousEffectiveLocationId, ct);
        MarkUserModified(camera);
        camera.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Replaces the camera's printer links with <paramref name="printerIds"/> while keeping exactly one
    /// primary camera per affected printer. The submitted order is the camera's link order: each link's
    /// <see cref="CameraPrinter.SortOrder"/> is set from its position, so the first printer becomes the
    /// representative printer. Links to <paramref name="preservedPrinterIds"/> (printers the caller cannot
    /// see) are kept and ordered after the requested printers.
    /// </summary>
    private async Task ApplyPrinterLinksAsync(
        Camera camera,
        IReadOnlyCollection<Guid> printerIds,
        bool makePrimary,
        IReadOnlyCollection<Guid>? preservedPrinterIds,
        CancellationToken ct)
    {
        List<Guid> requested = printerIds.Where(id => id != Guid.Empty).Distinct().ToList();
        HashSet<Guid> preserved = preservedPrinterIds is null ? [] : [.. preservedPrinterIds.Where(id => !requested.Contains(id))];
        List<Printer> printers = requested.Count == 0
            ? []
            : await _db.Printers.Include(p => p.Location).Where(p => requested.Contains(p.Id)).ToListAsync(ct);
        Guid? missing = requested.Cast<Guid?>().FirstOrDefault(id => printers.All(p => p.Id != id));
        if (missing.HasValue)
        {
            throw new ArgumentException($"Printer with ID '{missing.Value}' not found", nameof(printerIds));
        }

        foreach (CameraPrinter link in camera.PrinterLinks.Where(l => !requested.Contains(l.PrinterId) && !preserved.Contains(l.PrinterId)).ToList())
        {
            await RemoveLinkAsync(camera, link, ct);
        }

        int sortOrder = 0;
        foreach (Guid printerId in requested)
        {
            CameraPrinter? existing = camera.PrinterLinks.FirstOrDefault(l => l.PrinterId == printerId);
            if (existing is not null)
            {
                existing.SortOrder = sortOrder++;
                if (makePrimary && !existing.IsPrimary)
                {
                    existing.IsPrimary = true;
                    await DemoteOtherPrimariesAsync(printerId, camera.Id, ct);
                }

                continue;
            }

            bool primary = makePrimary || !await _unitOfWork.Cameras.HasPrimaryCameraAsync(printerId, ct);
            camera.LinkToPrinter(printerId, primary, sortOrder++).Printer = printers.First(p => p.Id == printerId);
            await ClearDismissalAsync(camera, printerId, ct);
            if (primary)
            {
                await DemoteOtherPrimariesAsync(printerId, camera.Id, ct);
            }
        }

        foreach (CameraPrinter hidden in camera.PrinterLinks
            .Where(l => preserved.Contains(l.PrinterId))
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.PrinterId)
            .ToList())
        {
            hidden.SortOrder = sortOrder++;
        }
    }

    /// <summary>
    /// Raises <c>camerasupdated</c> for each affected printer with its current linked camera ids, so open
    /// printer views refresh after a user-initiated change. No-op when SignalR is not available.
    /// </summary>
    private Task NotifyCamerasUpdatedAsync(IEnumerable<Guid> printerIds, CancellationToken ct) =>
        _hubContext is null
            ? Task.CompletedTask
            : CamerasUpdatedNotifier.NotifyCurrentLinksAsync(_hubContext, _db, printerIds, _logger, ct);

    private async Task RemoveLinkAsync(Camera camera, CameraPrinter link, CancellationToken ct)
    {
        if (link.IsPrimary)
        {
            await PromoteNextPrimaryAsync(link.PrinterId, camera.Id, ct);
        }

        camera.PrinterLinks.Remove(link);
        _unitOfWork.Cameras.RemoveLink(link);
        await DismissDiscoveredCameraAsync(camera, link.PrinterId, ct);
    }

    /// <summary>
    /// Remembers that the user removed an auto-discovered camera from a printer so background camera
    /// sync does not re-create it. Standalone and Prusa Buddy cameras are not sync-created and are skipped.
    /// </summary>
    private async Task DismissDiscoveredCameraAsync(Camera camera, Guid printerId, CancellationToken ct)
    {
        if (!PrinterCameraSynchronizer.IsDismissible(camera))
        {
            return;
        }

        DismissedPrinterCamera? dismissal = await FindDismissalAsync(printerId, camera.Source, ct);
        if (dismissal is null)
        {
            dismissal = new DismissedPrinterCamera
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                Source = camera.Source,
            };
            _db.DismissedPrinterCameras.Add(dismissal);
        }

        dismissal.Name = camera.Name;
        dismissal.DismissedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Manually linking a camera that occupies a printer's discovered-camera slot clears the matching
    /// dismissal, so the user's explicit choice wins over an earlier removal.
    /// </summary>
    private async Task ClearDismissalAsync(Camera camera, Guid printerId, CancellationToken ct)
    {
        if (!PrinterCameraSynchronizer.IsDismissible(camera))
        {
            return;
        }

        DismissedPrinterCamera? dismissal = await FindDismissalAsync(printerId, camera.Source, ct);
        if (dismissal is not null)
        {
            _db.DismissedPrinterCameras.Remove(dismissal);
        }
    }

    private async Task<DismissedPrinterCamera?> FindDismissalAsync(Guid printerId, CameraSource source, CancellationToken ct)
    {
        // Local covers dismissals added earlier in this unit of work (not yet saved).
        return _db.DismissedPrinterCameras.Local.FirstOrDefault(d => d.PrinterId == printerId && d.Source == source)
            ?? await _db.DismissedPrinterCameras.FirstOrDefaultAsync(d => d.PrinterId == printerId && d.Source == source, ct);
    }

    private async Task DemoteOtherPrimariesAsync(Guid printerId, Guid cameraId, CancellationToken ct)
    {
        IEnumerable<CameraPrinter> links = await _unitOfWork.Cameras.GetPrinterLinksAsync(printerId, ct);
        foreach (CameraPrinter other in links.Where(l => l.CameraId != cameraId && l.IsPrimary))
        {
            other.IsPrimary = false;
        }
    }

    /// <summary>
    /// Makes the next camera of the printer (excluding <paramref name="excludedCameraId"/>) its primary camera.
    /// Returns false when no other camera is linked to the printer.
    /// </summary>
    private async Task<bool> PromoteNextPrimaryAsync(Guid printerId, Guid excludedCameraId, CancellationToken ct)
    {
        List<CameraPrinter> others = (await _unitOfWork.Cameras.GetPrinterLinksAsync(printerId, ct))
            .Where(l => l.CameraId != excludedCameraId)
            .ToList();
        if (others.Count == 0)
        {
            return false;
        }

        if (!others.Any(l => l.IsPrimary))
        {
            CameraPrinter next = others
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Camera?.SortOrder ?? 0)
                .ThenBy(l => l.Camera?.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.CameraId)
                .First();
            next.IsPrimary = true;
        }

        return true;
    }

    /// <summary>
    /// Enforces the camera location rule: with exactly one linked printer the location derives from that
    /// printer and <see cref="Camera.LocationId"/> is cleared (any supplied value is ignored); otherwise the
    /// location is user-settable. When the camera stops deriving its location, the previously effective
    /// location is kept so the camera does not silently lose its placement.
    /// </summary>
    private async Task ApplyLocationRuleAsync(
        Camera camera,
        Guid? requestedLocationId,
        bool clearLocation,
        Guid? previousEffectiveLocationId,
        CancellationToken ct)
    {
        if (camera.PrinterLinks.Count == 1)
        {
            camera.LocationId = null;
            camera.AssignedLocation = null;
            return;
        }

        if (clearLocation)
        {
            camera.LocationId = null;
            camera.AssignedLocation = null;
            return;
        }

        Guid? target = requestedLocationId ?? camera.LocationId ?? previousEffectiveLocationId;
        if (target == camera.LocationId)
        {
            return;
        }

        if (target is Guid locationId)
        {
            Location location = await _db.Locations.FirstOrDefaultAsync(l => l.Id == locationId, ct)
                ?? throw new ArgumentException($"Location with ID '{locationId}' not found", nameof(requestedLocationId));
            camera.LocationId = location.Id;
            camera.AssignedLocation = location;
        }
    }

    private static void MarkUserModified(Camera camera)
    {
        // Only discovered cameras are re-synchronised from printers; flag user edits so sync preserves them.
        if (camera.Source != CameraSource.Standalone)
        {
            camera.UserModified = true;
        }
    }
}
