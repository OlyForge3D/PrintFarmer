using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Web.Api.Services.Gcode.Safety;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Gcode.Services.Gcode;

/// <summary>Content-bound consent and atomic local admission; never reads a remote source.</summary>
public sealed class PrinterFileQueueService(
    AppDbContext db,
    IPrinterFileAccessService access,
    IStoredFileOperationsService files,
    IPrintersRepository printers,
    IGcodeSafetyValidator safety,
    IPrinterFileTargetValidator targets,
    IPrinterFileQueueAdmission admission)
{
    public async Task<PrinterFileCopyOperation?> ConfirmAsync(
        Guid actorId, Guid operationId, PrinterFileSnapshotConfirmation confirmation, CancellationToken ct)
    {
        PrinterFileOperation? operation = await FindAsync(actorId, operationId, ct);
        if (operation is null)
        {
            return null;
        }

        if (operation.GcodeFileId != confirmation.GcodeFileId ||
            operation.ContentSha256 != confirmation.ContentSha256 ||
            operation.SizeBytes != confirmation.SizeBytes ||
            operation.PayloadSha256 != confirmation.PayloadSha256)
        {
            throw new PrinterFileCommandException("snapshot_confirmation_mismatch");
        }

        string binding = ConfirmationBinding(operation);
        if (operation.SnapshotConfirmationSha256 is not null)
        {
            if (!confirmation.Accept || operation.SnapshotConfirmationSha256 != binding)
            {
                throw new PrinterFileCommandException("snapshot_confirmation_mismatch");
            }

            return PrinterFileCopyService.View(operation);
        }

        if (operation.State != PrinterFileOperationState.AwaitingSourceConfirmation ||
            operation.CancellationRequested)
        {
            throw new PrinterFileCommandException("operation_not_awaiting_confirmation");
        }

        // Read the actual local file again before accepting any content-bound decision.
        await using FileStream stream = await OpenVerifiedAsync(operation, ct);
        if (!confirmation.Accept)
        {
            operation.State = PrinterFileOperationState.Cancelled;
            operation.CancellationRequested = true;
            operation.ReasonCode = "source_snapshot_declined";
        }
        else
        {
            operation.SnapshotConfirmationSha256 = binding;
            operation.SnapshotConfirmedAtUtc = DateTime.UtcNow;
            operation.State = PrinterFileOperationState.LocalReady;
            operation.ReasonCode = null;
        }

        operation.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            PrinterFileOperation? current = await FindAsync(actorId, operationId, ct);
            if (current is not null && confirmation.Accept && !current.CancellationRequested &&
                current.SnapshotConfirmationSha256 == binding)
            {
                return PrinterFileCopyService.View(current);
            }

            throw new PrinterFileCommandException("operation_changed");
        }

        return PrinterFileCopyService.View(operation);
    }

    public async Task<PrinterFileCopyOperation?> AdmitAsync(Guid actorId, Guid operationId, CancellationToken ct)
    {
        PrinterFileOperation? initial = await FindAsync(actorId, operationId, ct);
        if (initial is null)
        {
            return null;
        }

        if (initial.State == PrinterFileOperationState.Queued)
        {
            return PrinterFileCopyService.View(initial);
        }

        if (initial.State is not (PrinterFileOperationState.LocalReady or PrinterFileOperationState.Blocked) ||
            initial.CancellationRequested || initial.GcodeFileId is null)
        {
            throw new PrinterFileCommandException("operation_not_ready_for_admission");
        }

        try
        {
            await using FileStream stream = await OpenVerifiedAsync(initial, ct);
            using StreamReader reader = new(stream, new UTF8Encoding(true, true),
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            string text = await reader.ReadToEndAsync(ct);

            // Retain this exact local read handle through commit. No remote network operation
            // or ready/dispatch side effect occurs within this database transaction.
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
            {
                db.ChangeTracker.Clear();
                PrinterFileOperation operation = await FindAsync(actorId, operationId, ct)
                    ?? throw new PrinterFileCommandException("access_denied", 403);
                if (operation.State == PrinterFileOperationState.Queued)
                {
                    return PrinterFileCopyService.View(operation);
                }

                if (operation.Revision != initial.Revision || operation.CancellationRequested ||
                    operation.GcodeFileId != initial.GcodeFileId ||
                    operation.ContentSha256 != initial.ContentSha256 || operation.SizeBytes != initial.SizeBytes)
                {
                    throw new PrinterFileCommandException("operation_changed");
                }

                Printer destination = await printers.FindByIdWithIncludesAsync(operation.DestinationPrinterId!.Value, ct)
                    ?? throw new PrinterFileCommandException("destination_removed");
                GcodeFile local = await db.GcodeFiles.Include(x => x.PrinterModel)
                    .SingleOrDefaultAsync(x => x.Id == operation.GcodeFileId, ct)
                    ?? throw new PrinterFileCommandException("local_asset_removed");
                if (!destination.IsEnabled || !await targets.CanQueueAsync(local, destination.Id, ct))
                {
                    throw new PrinterFileCommandException("destination_incompatible");
                }

                GcodeSafetyResult<GcodeSafetyReport> validation = safety.Validate(new(
                    PrinterGcodeSafetyLimits.Build(destination), text, GcodeSafetyCheckpoint.BeforeQueueing));
                if (!validation.IsValid)
                {
                    throw new PrinterFileCommandException(validation.Problems[0].Code);
                }

                if (operation.Verification == PrinterFileVerification.UnverifiedSnapshot &&
                    (operation.SnapshotConfirmedAtUtc is null ||
                     operation.SnapshotConfirmationSha256 != ConfirmationBinding(operation)))
                {
                    operation.State = PrinterFileOperationState.AwaitingSourceConfirmation;
                    operation.ReasonCode = "unverified_source_snapshot";
                    operation.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return PrinterFileCopyService.View(operation);
                }

                // A CAS takes the destination write barrier so a removal/configuration edit
                // cannot race authoritative admission. Other dispatch/Ready gates stay intact.
                int fenced = await db.Printers.Where(x => x.Id == destination.Id &&
                    x.Revision == destination.Revision && x.IsEnabled)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
                if (fenced != 1)
                {
                    throw new PrinterFileCommandException("destination_changed");
                }

                PrinterFileQueueOptions options = PrinterFileCopyService.ReadSnapshot(operation).Command.Queue ?? new();
                operation.State = PrinterFileOperationState.Enqueueing;
                JobQueuePrintJobDto? job = await admission.AddAsync(operation, new()
                {
                    GcodeFileId = local.Id,
                    AssignedPrinterId = destination.Id,
                    Copies = options.Copies,
                    Priority = options.Priority,
                    DeadlineAtUtc = options.DeadlineAtUtc,
                    ProjectId = options.ProjectId,
                    ProjectFileId = options.ProjectFileId,
                    PlateIndex = options.PlateIndex,
                    PlateName = options.PlateName,
                }, ct);
                if (job is null)
                {
                    throw new PrinterFileCommandException("queue_admission_rejected");
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                initial = operation;
            }

            await admission.NotifyCommittedAsync(initial.DestinationPrinterId!.Value, ct);
            return PrinterFileCopyService.View(initial);
        }
        catch (DecoderFallbackException)
        {
            return await BlockAsync(actorId, operationId, "unsupported_gcode_encoding", ct);
        }
        catch (GcodeSafetyGeometryException)
        {
            return await BlockAsync(actorId, operationId, "destination_safety_geometry_invalid", ct);
        }
        catch (CalibrationQueueIncompatibleException)
        {
            return await BlockAsync(actorId, operationId, "queue_lineage_incompatible", ct);
        }
        catch (ValidationException)
        {
            return await BlockAsync(actorId, operationId, "queue_options_rejected", ct);
        }
        catch (PrinterFileCommandException failure)
        {
            return await BlockAsync(actorId, operationId, failure.Reason, ct);
        }
    }

    private async Task<PrinterFileCopyOperation?> BlockAsync(Guid actorId, Guid operationId, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        PrinterFileOperation? operation = await FindAsync(actorId, operationId, ct);
        if (operation is null || operation.State == PrinterFileOperationState.Queued)
        {
            return operation is null ? null : PrinterFileCopyService.View(operation);
        }

        if (!operation.CancellationRequested)
        {
            operation.State = PrinterFileOperationState.Blocked;
            operation.ReasonCode = reason;
            operation.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return PrinterFileCopyService.View(operation);
    }

    private async Task<PrinterFileOperation?> FindAsync(Guid actorId, Guid operationId, CancellationToken ct)
    {
        PrinterFileOperation? operation = await db.PrinterFileOperations.Include(x => x.Jobs)
            .SingleOrDefaultAsync(
                x => x.Id == operationId && x.ActorId == actorId &&
                x.Action == PrinterFileAction.Queue, ct);
        return operation is not null &&
            await access.CanActAsync(
                actorId, operation.SourcePrinterId,
                PrinterFileAction.Queue, operation.DestinationPrinterId, ct) &&
            (!operation.GcodeFileId.HasValue || await access.CanAccessLocalAsync(
                actorId, operation.GcodeFileId.Value, PrinterGroupAccessLevel.Submit, ct)) ? operation : null;
    }

    private async Task<FileStream> OpenVerifiedAsync(PrinterFileOperation operation, CancellationToken ct)
    {
        GcodeFile file = await db.GcodeFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operation.GcodeFileId, ct)
            ?? throw new PrinterFileCommandException("local_asset_removed");
        FileStream stream;
        try
        {
            stream = new(
                files.GetFullFilePath(file), FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException)
        {
            throw new PrinterFileCommandException("local_bytes_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            throw new PrinterFileCommandException("local_bytes_unavailable");
        }

        try
        {
            StoredGcodeIntegrityResult result = await StoredGcodeIntegrityVerifier.VerifyOpenedStreamAsync(
                stream, operation.ContentSha256!, operation.SizeBytes, ct);
            if (!result.Success)
            {
                throw new PrinterFileCommandException(result.ErrorCode!);
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static string ConfirmationBinding(PrinterFileOperation operation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            operation.ActorId,
            operation.Id,
            operation.GcodeFileId,
            operation.ContentSha256,
            operation.SizeBytes,
            operation.PayloadSha256,
        }))));
}
