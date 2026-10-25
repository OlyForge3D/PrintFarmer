using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>
/// Durable commands and narrow copy-only quota ledger. ReservedBytes is an outstanding
/// reservation until publication, then the newly-owned byte charge (zero for dedup).
/// Unbound reservations use UpdatedAtUtc as their accounting-day watermark. Before I/O
/// they are rebooked transactionally to today's budget. Settled charges use the immutable
/// origin capture day, so later queue-operation updates cannot move historical charges.
/// Legacy uploads retain their existing accounting; this does not make them globally transactional.
/// </summary>
public sealed class PrinterFileCopyService(
    AppDbContext db,
    IPrinterFileAccessService access,
    ISettingsService settings,
    IStoragePathService storage) : IPrinterFileCopyService
{
    public const long MaximumBytes = 200_000_000;

    public async Task<IReadOnlyList<PrinterFileCopyOperation>> ListAsync(Guid actorId, CancellationToken ct = default)
    {
        if (actorId == Guid.Empty)
        {
            return [];
        }

        PrinterFileOperation[] recent = await db.PrinterFileOperations.Include(x => x.Jobs).AsNoTracking()
            .Where(x => x.ActorId == actorId && (x.Action == PrinterFileAction.Copy || x.Action == PrinterFileAction.Queue))
            .OrderByDescending(x => x.CreatedAtUtc).Take(100).ToArrayAsync(ct);
        List<PrinterFileCopyOperation> visible = [];
        foreach (PrinterFileOperation operation in recent)
        {
            bool permitted = operation.Action == PrinterFileAction.Queue
                ? await access.CanActAsync(actorId, operation.SourcePrinterId, operation.Action, operation.DestinationPrinterId, ct)
                : await access.CanReadAsync(actorId, operation.SourcePrinterId, ct);
            if (permitted && await CanRevealLocalBindingAsync(operation, ct))
            {
                visible.Add(View(operation));
            }
        }

        return visible;
    }

    public async Task<PrinterFileCopyOperation> CreateAsync(
        Guid actorId, string idempotencyKey, PrinterFileCopyCommand command, CancellationToken ct = default)
    {
        if (actorId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 256 || idempotencyKey.Any(char.IsControl))
        {
            throw new PrinterFileCommandException("invalid_idempotency_key", 400);
        }

        if (command.Action is not (PrinterFileAction.Copy or PrinterFileAction.Queue) ||
            (command.Action == PrinterFileAction.Copy && (command.DestinationPrinterId is not null || command.Queue is not null)) ||
            (command.Action == PrinterFileAction.Queue && command.DestinationPrinterId is null))
        {
            throw new PrinterFileCommandException("invalid_copy_action", 400);
        }

        if (command.Action == PrinterFileAction.Queue)
        {
            command = command with { Queue = command.Queue ?? new() };
            if (command.Queue.Copies is < 1 or > 1000 ||
                !Enum.IsDefined(command.Queue.Priority))
            {
                throw new PrinterFileCommandException("invalid_queue_options", 400);
            }
        }

        command = command with { VirtualDirectory = NormalizeDirectory(command.VirtualDirectory) };
        string keyHash = Hash(idempotencyKey);
        string payloadHash = Hash(JsonSerializer.Serialize(command));
        PrinterFileOperation? replay = await db.PrinterFileOperations.Include(x => x.Jobs).AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorId == actorId && x.CommandKeyHash == keyHash, ct);
        if (replay is not null)
        {
            return await ReplayAsync(replay, payloadHash, ct);
        }

        PrinterFileLocation location = await db.PrinterFileLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == command.LocationId, ct)
            ?? throw new PrinterFileCommandException("location_not_found", 404);
        if (!await access.CanActAsync(actorId, location.SourcePrinterId, command.Action,
                command.DestinationPrinterId, ct))
        {
            throw new PrinterFileCommandException("access_denied", 403);
        }

        if (location.RegisteredPrinterId != location.SourcePrinterId ||
            location.Availability != PrinterFileAvailability.Available ||
            location.ObservationRevision != command.ObservationRevision)
        {
            throw new PrinterFileCommandException("stale_observation");
        }

        await ValidateExtensionAsync(location.RelativePath, ct);
        if (!PrinterInventoryPath.TryCanonicalizeRoot(location.StorageRoot, out string root) ||
            root != location.StorageRoot ||
            !PrinterInventoryPath.TryCanonicalizeRelativePath(location.RelativePath, out _) ||
            location.RelativePath.Contains('%'))
        {
            throw new PrinterFileCommandException("invalid_source_path", 400);
        }

        long reservation = ReservationSize(location.SizeBytes);
        DateTime now = DateTime.UtcNow;
        PrinterFileOperation operation = new()
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            CommandKeyHash = keyHash,
            PayloadSha256 = payloadHash,
            SourcePrinterId = location.SourcePrinterId,
            LocationId = location.Id,
            ObservationRevision = location.ObservationRevision,
            ConfigurationGeneration = location.ConfigurationGeneration,
            MutationGeneration = location.MutationGeneration,
            Action = command.Action,
            DestinationPrinterId = command.DestinationPrinterId,
            State = PrinterFileOperationState.Pending,
            CommandJson = JsonSerializer.Serialize(new CopySnapshot(
                command, root, location.RelativePath, location.SizeBytes,
                location.ModifiedValue, location.ProviderVersion, location.ProviderDigest)),
            ReservedBytes = reservation,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            operation.UpdatedAtUtc = await CheckQuotaAsync(operation, reservation, ct);
            db.PrinterFileOperations.Add(operation);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            replay = await db.PrinterFileOperations.Include(x => x.Jobs).AsNoTracking()
                .SingleOrDefaultAsync(x => x.ActorId == actorId && x.CommandKeyHash == keyHash, ct);
            if (replay is null)
            {
                throw;
            }

            return await ReplayAsync(replay, payloadHash, ct);
        }

        return View(operation);
    }

    public async Task<PrinterFileCopyOperation?> GetAsync(
        Guid actorId, Guid operationId, CancellationToken ct = default)
    {
        PrinterFileOperation? operation = await FindOwnedAsync(actorId, operationId, false, ct);
        return operation is null ? null : View(operation);
    }

    public async Task<PrinterFileCopyOperation?> RetryAsync(
        Guid actorId, Guid operationId, CancellationToken ct = default)
    {
        PrinterFileOperation? operation = await FindOwnedAsync(actorId, operationId, true, ct);
        if (operation is null)
        {
            return null;
        }

        // A capture binding is immutable. Queue confirmation/admission is a separate service.
        if (operation.ContentSha256 is not null || operation.GcodeFileId is not null ||
            operation.SnapshotConfirmationSha256 is not null ||
            operation.State is not (PrinterFileOperationState.Failed or PrinterFileOperationState.Blocked))
        {
            throw new PrinterFileCommandException("operation_not_retryable");
        }

        CopySnapshot snapshot = ReadSnapshot(operation);
        long reservation = ReservationSize(snapshot.SizeBytes);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        DateTime accountingAtUtc = await CheckQuotaAsync(operation, reservation, ct);
        operation.State = PrinterFileOperationState.Pending;
        operation.CancellationRequested = false;
        operation.ReasonCode = null;
        operation.ReservedBytes = reservation;
        operation.WorkerId = null;
        operation.WorkerLeaseExpiresAtUtc = null;
        operation.UpdatedAtUtc = accountingAtUtc;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return View(operation);
    }

    public async Task<PrinterFileCopyOperation?> CancelAsync(
        Guid actorId, Guid operationId, CancellationToken ct = default)
    {
        PrinterFileOperation? operation = await FindOwnedAsync(actorId, operationId, true, ct);
        if (operation is null)
        {
            return null;
        }

        if (operation.State is PrinterFileOperationState.Pending or PrinterFileOperationState.LocalReady
            or PrinterFileOperationState.AwaitingSourceConfirmation or PrinterFileOperationState.Blocked
            or PrinterFileOperationState.Failed)
        {
            operation.State = PrinterFileOperationState.Cancelled;
            if (operation.ContentSha256 is null)
            {
                operation.ReservedBytes = 0;
            }
        }
        else if (operation.State == PrinterFileOperationState.Queued)
        {
            return View(operation);
        }
        else if (operation.State is not (PrinterFileOperationState.Copying or PrinterFileOperationState.Validating
                     or PrinterFileOperationState.Cancelled))
        {
            throw new PrinterFileCommandException("operation_not_cancellable");
        }

        operation.CancellationRequested = true;
        if (operation.ReservedBytes == 0)
        {
            operation.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return View(operation);
    }

    internal async Task<DateTime> CheckQuotaAsync(PrinterFileOperation operation, long reservation, CancellationToken ct)
    {
        DateTime accountingAtUtc = DateTime.UtcNow;
        DateTime day = accountingAtUtc.Date;
        DateTime nextDay = day.AddDays(1);
        long held = await db.PrinterFileOperations
            .Where(x => x.Id != operation.Id && x.ActorId == operation.ActorId &&
                x.ContentSha256 == null && x.UpdatedAtUtc >= day && x.UpdatedAtUtc < nextDay &&
                (x.Action == PrinterFileAction.Copy || x.Action == PrinterFileAction.Queue))
            .SumAsync(x => (long?)x.ReservedBytes, ct) ?? 0;
        long settled = await (
            from origin in db.PrinterFileOrigins
            join charged in db.PrinterFileOperations on origin.OperationId equals charged.Id
            where charged.Id != operation.Id && charged.ActorId == operation.ActorId &&
                origin.CapturedAtUtc >= day && origin.CapturedAtUtc < nextDay
            select (long?)charged.ReservedBytes).SumAsync(ct) ?? 0;
        long accounted = checked(held + settled);
        long limit = (await ReadUploadPolicyAsync(ct)).DailyUploadLimitBytes;
        if (reservation > 0 && reservation > limit - accounted)
        {
            throw new PrinterFileCommandException("daily_quota_exceeded", 429);
        }

        long pending = await db.PrinterFileOperations
            .Where(x => x.Id != operation.Id &&
                (x.Action == PrinterFileAction.Copy || x.Action == PrinterFileAction.Queue) &&
                (x.State == PrinterFileOperationState.Pending || x.State == PrinterFileOperationState.Copying ||
                 x.State == PrinterFileOperationState.Validating))
            .SumAsync(x => (long?)x.ReservedBytes, ct) ?? 0;
        string directory = storage.GetGcodeStorageDirectory();
        Directory.CreateDirectory(directory);
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        if (reservation > 0 && reservation > free - pending)
        {
            throw new PrinterFileCommandException("storage_capacity_exceeded", 507);
        }

        return accountingAtUtc;
    }

    internal async Task ReserveForAttemptAsync(
        Guid operationId, Guid workerId, long fence, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        PrinterFileOperation operation = await db.PrinterFileOperations.SingleAsync(x => x.Id == operationId, ct);
        if (operation.WorkerId != workerId || operation.WorkerFence != fence ||
            operation.CancellationRequested || operation.ContentSha256 is not null)
        {
            throw new PrinterFileCommandException(operation.CancellationRequested ? "cancelled" : "worker_superseded");
        }

        long reservation = ReservationSize(ReadSnapshot(operation).SizeBytes);
        DateTime accountingAtUtc = await CheckQuotaAsync(operation, reservation, ct);
        operation.ReservedBytes = reservation;
        operation.UpdatedAtUtc = accountingAtUtc;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<PrinterFileOperation?> FindOwnedAsync(
        Guid actorId, Guid id, bool mutation, CancellationToken ct)
    {
        if (actorId == Guid.Empty)
        {
            return null;
        }

        PrinterFileOperation? operation = await db.PrinterFileOperations.Include(x => x.Jobs)
            .SingleOrDefaultAsync(
                x => x.Id == id && x.ActorId == actorId &&
                (x.Action == PrinterFileAction.Copy || x.Action == PrinterFileAction.Queue), ct);
        if (operation is null ||
            !await access.CanReadAsync(actorId, operation.SourcePrinterId, ct) ||
            ((mutation || operation.Action == PrinterFileAction.Queue) && !await access.CanActAsync(
                actorId, operation.SourcePrinterId,
                operation.Action, operation.DestinationPrinterId, ct)) ||
            !await CanRevealLocalBindingAsync(operation, ct))
        {
            return null;
        }

        return operation;
    }

    private async Task<PrinterFileCopyOperation> ReplayAsync(
        PrinterFileOperation operation, string payloadHash, CancellationToken ct)
    {
        if (!await access.CanActAsync(operation.ActorId, operation.SourcePrinterId, operation.Action,
                operation.DestinationPrinterId, ct))
        {
            throw new PrinterFileCommandException("access_denied", 403);
        }

        if (!await CanRevealLocalBindingAsync(operation, ct))
        {
            // Do not disclose the binding or even payload-conflict details after local revocation.
            throw new PrinterFileCommandException("operation_not_found", 404);
        }

        if (operation.PayloadSha256 != payloadHash)
        {
            throw new PrinterFileCommandException("idempotency_payload_mismatch");
        }

        return View(operation);
    }

    private Task<bool> CanRevealLocalBindingAsync(PrinterFileOperation operation, CancellationToken ct) =>
        operation.GcodeFileId is Guid assetId
            ? access.CanAccessLocalAsync(
                operation.ActorId, assetId,
                operation.Action == PrinterFileAction.Queue ? PrinterGroupAccessLevel.Submit : PrinterGroupAccessLevel.View, ct)
            : Task.FromResult(operation.ContentSha256 is null);

    internal async Task ValidateExtensionAsync(string path, CancellationToken ct)
    {
        GcodeUploadSettings policy = await ReadUploadPolicyAsync(ct);
        if (!policy.AllowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            throw new PrinterFileCommandException("extension_not_allowed", 400);
        }
    }

    private async Task<GcodeUploadSettings> ReadUploadPolicyAsync(CancellationToken ct)
    {
        string? json = await db.AppSettingsEntities.AsNoTracking()
            .Where(x => x.Key == GcodeUploadSettings.SectionName)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        try
        {
            // Prefer current persisted policy across replicas, not a retained process cache.
            GcodeUploadSettings policy = json is null
                ? settings.Get<GcodeUploadSettings>() ?? new()
                : JsonSerializer.Deserialize<GcodeUploadSettings>(json)
                    ?? throw new PrinterFileCommandException("upload_policy_unavailable", 503);
            policy.Validate();
            return policy;
        }
        catch (JsonException)
        {
            throw new PrinterFileCommandException("upload_policy_unavailable", 503);
        }
    }

    internal static long ReservationSize(long? size) => size switch
    {
        < 0 or > MaximumBytes => throw new PrinterFileCommandException("size_limit", 413),
        0 => throw new PrinterFileCommandException("empty_source", 400),
        null => MaximumBytes,
        _ => size.Value,
    };

    internal static string NormalizeDirectory(string? value)
    {
        string path = (value ?? "/").Trim('/');
        if (path.Length == 0)
        {
            return "/";
        }

        if (!PrinterInventoryPath.TryCanonicalizeRelativePath(path, out _) ||
            path.Contains('%') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.Split('/').Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new PrinterFileCommandException("invalid_library_directory", 400);
        }

        return "/" + path;
    }

    internal static CopySnapshot ReadSnapshot(PrinterFileOperation operation) =>
        JsonSerializer.Deserialize<CopySnapshot>(operation.CommandJson)
        ?? throw new PrinterFileCommandException("invalid_saved_command");

    internal static PrinterFileCopyOperation View(PrinterFileOperation operation) => new(
        operation.Id, operation.Revision, operation.Action, operation.State, operation.LocationId,
        operation.GcodeFileId, operation.ContentSha256, operation.SizeBytes,
        operation.Verification, operation.ReasonCode, operation.CancellationRequested,
        operation.DestinationPrinterId, operation.PayloadSha256,
        operation.Jobs.OrderBy(x => x.Ordinal).Select(x => x.JobId).ToArray());

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal sealed record CopySnapshot(
        PrinterFileCopyCommand Command, string StorageRoot, string RelativePath,
        long? SizeBytes, string? ModifiedValue, string? ProviderVersion, string? ProviderDigest);
}
