using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Coordinates authoritative family persistence with the worker's derived Custom bundle.
/// </summary>
public sealed class ProfileFamilyService(
    SlicerDbContext dbContext,
    ICatalogServiceAdapter catalogService,
    IPrinterModelAliasService aliasService,
    IProfileFamilyRenderer renderer,
    IProfileFamilyWorkerClient workerClient,
    ILogger<ProfileFamilyService> logger) : IProfileFamilyService
{
    private readonly SlicerDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICatalogServiceAdapter _catalogService =
        catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    private readonly IPrinterModelAliasService _aliasService =
        aliasService ?? throw new ArgumentNullException(nameof(aliasService));

    private readonly IProfileFamilyRenderer _renderer =
        renderer ?? throw new ArgumentNullException(nameof(renderer));

    private readonly IProfileFamilyWorkerClient _workerClient =
        workerClient ?? throw new ArgumentNullException(nameof(workerClient));

    private readonly ILogger<ProfileFamilyService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<CloneProfileFamilyResponseDto> CloneFamilyAsync(
        CloneProfileFamilyRequestDto request,
        Guid userId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user identity is required.", nameof(userId));
        }

        string familyName = request.FamilyName.Trim();
        if (string.IsNullOrWhiteSpace(familyName))
        {
            throw new ArgumentException("familyName is required.", nameof(request));
        }

        if (request.TargetPrinterModelId == Guid.Empty)
        {
            throw new ArgumentException("targetPrinterModelId is required.", nameof(request));
        }

        CatalogModelInfo? targetModel = await _catalogService.GetModelByIdAsync(
            request.TargetPrinterModelId,
            ct);
        if (targetModel is null)
        {
            throw new ArgumentException(
                $"Target printer model '{request.TargetPrinterModelId}' was not found.",
                nameof(request));
        }

        await EnsureNameAvailableAsync(familyName, request.TargetPrinterModelId, ct);

        (ProfileFamilyWorkerTarget worker, AllProfilesResponseDto catalog) =
            await _workerClient.GetCatalogAsync(
                request.SourceManufacturer,
                request.SlicerEngineVersion,
                ct);

        Guid familyId = Guid.NewGuid();
        CloneProfileFamilyRequestDto normalizedRequest = request;
        normalizedRequest.FamilyName = familyName;
        ProfileFamilyRenderResult rendered = _renderer.Render(
            familyId,
            normalizedRequest,
            catalog);
        string familyHash = ComputeHash(
            familyName,
            $"{request.SourceManufacturer.Trim()}/{request.SourceMachineModelName.Trim()}",
            rendered.CanonicalFamilyOverridesJson);
        DateTime now = DateTime.UtcNow;

        MachineModelProfile family = new()
        {
            Id = familyId,
            Name = familyName,
            Manufacturer = "Custom",
            Description = $"Custom OrcaSlicer profile family for {targetModel.Name}",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = request.TargetPrinterModelId,
            Hash = familyHash,
            IsSystem = false,
            IsPublic = true,
            SlicerVersion = worker.OrcaVersion,
            SlicerDistribution = request.SlicerDistribution.Trim(),
            SourceMachineModelName = request.SourceMachineModelName.Trim(),
            FamilyOverridesJson = rendered.CanonicalFamilyOverridesJson,
            CreatedByUserId = userId,
            RenderStatus = ProfileFamilyRenderStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        List<MachineProfile> machineProfiles = rendered.MachineVariants
            .Select(variant => new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = variant.Name,
                Manufacturer = "Custom",
                Description = $"Generated {FormatNozzle(variant.NozzleDiameter)} mm variant for {familyName}",
                SlicerType = SlicerType.OrcaSlicer,
                PrinterModelId = request.TargetPrinterModelId,
                MachineModelProfileId = familyId,
                Hash = ComputeHash(familyHash, variant.SourceSystemPresetName, variant.OverridesJson),
                IsSystem = false,
                IsDefault = false,
                IsPublic = true,
                SlicerVersion = worker.OrcaVersion,
                SlicerDistribution = request.SlicerDistribution.Trim(),
                SourceSystemPresetName = variant.SourceSystemPresetName,
                OverridesJson = variant.OverridesJson,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();

        await PersistFamilyAsync(family, machineProfiles, ct);

        try
        {
            await _workerClient.WriteBundleAsync(worker, rendered.Bundle, ct);
            try
            {
                await _aliasService.EnsureModelAliasAsync(
                    request.TargetPrinterModelId,
                    familyName,
                    "OrcaSlicer",
                    ct);
            }
            catch (InvalidOperationException ex)
            {
                throw new ProfileFamilyConflictException(ex.Message, ex);
            }

            family.RenderStatus = ProfileFamilyRenderStatus.Healthy;
            family.LastRenderedAt = DateTime.UtcNow;
            family.RenderedForOrcaVersion = worker.OrcaVersion;
            family.UpdatedAt = family.LastRenderedAt.Value;
            _ = await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            family.RenderStatus = ProfileFamilyRenderStatus.Failed;
            family.UpdatedAt = DateTime.UtcNow;
            _ = await _dbContext.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(
                ex,
                "Failed to install generated profile family {FamilyId} for OrcaSlicer {Version}",
                family.Id,
                worker.OrcaVersion);
            throw;
        }

        IReadOnlyList<ProfileFamilyMachineVariantDto> machineDtos = machineProfiles
            .Select((profile, index) => new ProfileFamilyMachineVariantDto(
                profile.Id,
                profile.Name,
                rendered.MachineVariants[index].NozzleDiameter,
                profile.SourceSystemPresetName!))
            .ToList();

        return new CloneProfileFamilyResponseDto(
            family.Id,
            family.Name,
            request.TargetPrinterModelId,
            family.RenderStatus,
            family.LastRenderedAt,
            machineDtos,
            rendered.ProcessProfileCount,
            rendered.FilamentProfileCount);
    }

    private async Task EnsureNameAvailableAsync(
        string familyName,
        Guid targetPrinterModelId,
        CancellationToken ct)
    {
        List<string> existingNames = await _dbContext.MachineModelProfiles
            .AsNoTracking()
            .Where(profile => profile.SlicerType == SlicerType.OrcaSlicer)
            .Select(profile => profile.Name)
            .ToListAsync(ct);
        bool exists = existingNames.Contains(
            familyName,
            StringComparer.OrdinalIgnoreCase);
        if (exists)
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{familyName}' already exists.");
        }

        Guid? existingAliasTarget = await _aliasService.ResolveModelAliasAsync(
            familyName,
            "OrcaSlicer");
        if (existingAliasTarget.HasValue && existingAliasTarget.Value != targetPrinterModelId)
        {
            throw new ProfileFamilyConflictException(
                $"OrcaSlicer model name '{familyName}' is already mapped to another printer model.");
        }
    }

    private async Task PersistFamilyAsync(
        MachineModelProfile family,
        IReadOnlyCollection<MachineProfile> machineProfiles,
        CancellationToken ct)
    {
        await using IDbContextTransaction transaction =
            await _dbContext.Database.BeginTransactionAsync(ct);
        _ = _dbContext.MachineModelProfiles.Add(family);
        _dbContext.MachineProfiles.AddRange(machineProfiles);
        try
        {
            _ = await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{family.Name}' already exists.",
                ex);
        }
    }

    private static string ComputeHash(params string[] values)
    {
        string input = string.Join('\n', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string FormatNozzle(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
