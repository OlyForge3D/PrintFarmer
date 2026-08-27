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

        CloneProfileFamilyRequestDto normalizedRequest = CopyRequest(request, familyName);
        MachineModelProfile? failedFamily = await FindRetryableFailedFamilyAsync(
            normalizedRequest,
            ct);

        (ProfileFamilyWorkerTarget worker, AllProfilesResponseDto catalog) =
            await _workerClient.GetCatalogAsync(
                normalizedRequest.SourceManufacturer,
                normalizedRequest.SlicerEngineVersion,
                ct);

        Guid familyId = failedFamily?.Id ?? Guid.NewGuid();
        ProfileFamilyRenderResult rendered = _renderer.Render(
            familyId,
            normalizedRequest,
            catalog);
        string familyHash = ComputeHash(
            familyName,
            $"{normalizedRequest.SourceManufacturer.Trim()}/{normalizedRequest.SourceMachineModelName.Trim()}",
            rendered.CanonicalFamilyOverridesJson);
        DateTime now = DateTime.UtcNow;

        MachineModelProfile family = failedFamily ?? new MachineModelProfile
        {
            Id = familyId,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        family.Name = familyName;
        family.Manufacturer = "Custom";
        family.Description = $"Custom OrcaSlicer profile family for {targetModel.Name}";
        family.SlicerType = SlicerType.OrcaSlicer;
        family.PrinterModelId = normalizedRequest.TargetPrinterModelId;
        family.Hash = familyHash;
        family.IsSystem = false;
        family.IsPublic = true;
        family.SlicerVersion = worker.OrcaVersion;
        family.SlicerDistribution = normalizedRequest.SlicerDistribution.Trim();
        family.SourceMachineModelName = normalizedRequest.SourceMachineModelName.Trim();
        family.FamilyOverridesJson = rendered.CanonicalFamilyOverridesJson;
        family.CreatedByUserId ??= userId;
        family.RenderStatus = ProfileFamilyRenderStatus.Pending;
        family.LastRenderedAt = null;
        family.RenderedForOrcaVersion = null;
        family.UpdatedAt = now;

        List<MachineProfile> machineProfiles = rendered.MachineVariants
            .Select(variant => new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = variant.Name,
                Manufacturer = "Custom",
                Description = $"Generated {FormatNozzle(variant.NozzleDiameter)} mm variant for {familyName}",
                SlicerType = SlicerType.OrcaSlicer,
                PrinterModelId = normalizedRequest.TargetPrinterModelId,
                MachineModelProfileId = familyId,
                Hash = ComputeHash(familyHash, variant.SourceSystemPresetName, variant.OverridesJson),
                IsSystem = false,
                IsDefault = false,
                IsPublic = true,
                SlicerVersion = worker.OrcaVersion,
                SlicerDistribution = normalizedRequest.SlicerDistribution.Trim(),
                SourceSystemPresetName = variant.SourceSystemPresetName,
                OverridesJson = variant.OverridesJson,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToList();

        await PersistFamilyAsync(family, machineProfiles, failedFamily is not null, ct);

        try
        {
            await _workerClient.WriteBundleAsync(worker, rendered.Bundle, ct);
            try
            {
                await _aliasService.EnsureModelAliasAsync(
                    normalizedRequest.TargetPrinterModelId,
                    familyName,
                    "OrcaSlicer",
                    ct);
                await _catalogService.InvalidateModelAliasesAsync(
                    normalizedRequest.TargetPrinterModelId,
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
        catch (Exception ex)
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
            normalizedRequest.TargetPrinterModelId,
            family.RenderStatus,
            family.LastRenderedAt,
            machineDtos,
            rendered.ProcessProfileCount,
            rendered.FilamentProfileCount);
    }

    private async Task<MachineModelProfile?> FindRetryableFailedFamilyAsync(
        CloneProfileFamilyRequestDto request,
        CancellationToken ct)
    {
        string normalizedName = MachineModelProfile.NormalizeNameKey(request.FamilyName);
        List<MachineModelProfile> matchingFamilies = await _dbContext.MachineModelProfiles
            .Where(profile =>
                profile.SlicerType == SlicerType.OrcaSlicer
                && profile.NameNormalized == normalizedName)
            .Take(2)
            .ToListAsync(ct);

        if (matchingFamilies.Count > 1)
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{request.FamilyName}' already exists.");
        }

        MachineModelProfile? retryableFamily = matchingFamilies.SingleOrDefault();
        if (retryableFamily is not null
            && (retryableFamily.IsSystem
                || retryableFamily.RenderStatus is not (
                    ProfileFamilyRenderStatus.Failed or ProfileFamilyRenderStatus.Pending)
                || retryableFamily.PrinterModelId != request.TargetPrinterModelId))
        {
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{request.FamilyName}' already exists.");
        }

        Guid? existingAliasTarget = await _aliasService.ResolveModelAliasAsync(
            request.FamilyName,
            "OrcaSlicer");
        if (existingAliasTarget.HasValue
            && existingAliasTarget.Value != request.TargetPrinterModelId)
        {
            throw new ProfileFamilyConflictException(
                $"OrcaSlicer model name '{request.FamilyName}' is already mapped to another printer model.");
        }

        return retryableFamily;
    }

    private async Task PersistFamilyAsync(
        MachineModelProfile family,
        IReadOnlyCollection<MachineProfile> machineProfiles,
        bool replaceExisting,
        CancellationToken ct)
    {
        await using IDbContextTransaction transaction =
            await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            if (replaceExisting)
            {
                List<MachineProfile> existingProfiles = await _dbContext.MachineProfiles
                    .Where(profile => profile.MachineModelProfileId == family.Id)
                    .ToListAsync(ct);
                _dbContext.MachineProfiles.RemoveRange(existingProfiles);
                _ = await _dbContext.SaveChangesAsync(ct);
            }
            else
            {
                _ = _dbContext.MachineModelProfiles.Add(family);
            }

            _dbContext.MachineProfiles.AddRange(machineProfiles);
            _ = await _dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsFamilyNameUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new ProfileFamilyConflictException(
                $"A slicer profile family named '{family.Name}' already exists.",
                ex);
        }
        catch (DbUpdateException ex) when (IsFamilyHashUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            MachineModelProfile? collidingFamily = await _dbContext.MachineModelProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(profile => profile.Hash == family.Hash, CancellationToken.None);
            throw new ProfileFamilyHashConflictException(
                collidingFamily is null
                    ? $"A slicer profile family with the same rendered content already exists (family '{family.Name}')."
                    : $"A slicer profile family with the same rendered content already exists: '{collidingFamily.Name}'.",
                ex);
        }
        catch (DbUpdateException ex) when (IsMachineProfileHashUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);

            // Materialize the candidate hashes before querying: relying on EF to translate a
            // nested `machineProfiles.Select(...).Contains(...)` closure risks an
            // InvalidOperationException escaping this catch clause and reverting the caller to
            // the raw 500 this fix exists to avoid (review finding).
            List<string> candidateHashes = [.. machineProfiles
                .Where(candidate => candidate.Hash is not null)
                .Select(candidate => candidate.Hash!)];
            MachineProfile? collidingProfile = await _dbContext.MachineProfiles
                .AsNoTracking()
                .Where(profile => profile.Hash != null && candidateHashes.Contains(profile.Hash))
                .FirstOrDefaultAsync(CancellationToken.None);
            throw new ProfileFamilyHashConflictException(
                collidingProfile is null
                    ? $"A machine profile with the same rendered content already exists (family '{family.Name}')."
                    : $"A machine profile with the same rendered content already exists: '{collidingProfile.SourceSystemPresetName}'.",
                ex);
        }
    }

    private static CloneProfileFamilyRequestDto CopyRequest(
        CloneProfileFamilyRequestDto request,
        string familyName)
    {
        return new CloneProfileFamilyRequestDto
        {
            FamilyName = familyName,
            TargetPrinterModelId = request.TargetPrinterModelId,
            SourceManufacturer = request.SourceManufacturer,
            SourceMachineModelName = request.SourceMachineModelName,
            NozzleDiameters = [.. request.NozzleDiameters],
            FamilyOverrides = new Dictionary<string, System.Text.Json.JsonElement>(
                request.FamilyOverrides,
                StringComparer.Ordinal),
            SlicerEngineVersion = request.SlicerEngineVersion,
            SlicerDistribution = request.SlicerDistribution
        };
    }

    private static bool IsFamilyNameUniqueConstraintViolation(DbUpdateException exception) =>
        IsUniqueConstraintViolation(
            exception,
            "IX_MachineModelProfiles_Name_SlicerType",
            "MachineModelProfiles.NameNormalized, MachineModelProfiles.SlicerType");

    private static bool IsFamilyHashUniqueConstraintViolation(DbUpdateException exception) =>
        IsUniqueConstraintViolation(
            exception,
            "IX_MachineModelProfiles_Hash",
            "MachineModelProfiles.Hash");

    private static bool IsMachineProfileHashUniqueConstraintViolation(DbUpdateException exception) =>
        IsUniqueConstraintViolation(
            exception,
            "IX_MachineProfiles_Hash",
            "MachineProfiles.Hash");

    /// <summary>
    /// #2080: shared unique-constraint detection for <see cref="PersistFamilyAsync"/>'s catch
    /// clauses -- covers SQLite (extended error code + column-list message), Postgres
    /// (SqlState 23505 + ConstraintName), and SqlServer (error 2601/2627 + message) so a
    /// content-hash collision is reported the same way a family-name collision already is,
    /// instead of surfacing as a raw 500.
    /// </summary>
    private static bool IsUniqueConstraintViolation(
        DbUpdateException exception,
        string indexName,
        string sqliteColumnsSubstring)
    {
        for (Exception? inner = exception.InnerException;
             inner is not null;
             inner = inner.InnerException)
        {
            if (inner is Microsoft.Data.Sqlite.SqliteException sqlite
                && sqlite.SqliteExtendedErrorCode is 1555 or 2067)
            {
                return sqlite.Message.Contains(
                    sqliteColumnsSubstring,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (inner is System.Data.Common.DbException dbException
                && string.Equals(dbException.SqlState, "23505", StringComparison.Ordinal))
            {
                string? constraintName =
                    inner.GetType().GetProperty("ConstraintName")?.GetValue(inner) as string;
                return string.Equals(
                    constraintName,
                    indexName,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (inner.GetType().FullName is
                    "Microsoft.Data.SqlClient.SqlException" or
                    "System.Data.SqlClient.SqlException"
                && inner.GetType().GetProperty("Number")?.GetValue(inner) is int number
                && number is 2601 or 2627)
            {
                return inner.Message.Contains(
                    indexName,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static string ComputeHash(params string[] values)
    {
        string input = string.Join('\n', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static string FormatNozzle(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
