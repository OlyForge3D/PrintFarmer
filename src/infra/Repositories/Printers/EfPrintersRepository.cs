using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Farm.Infrastructure.Repositories.Printers;

public class EfPrintersRepository(AppDbContext db, ISensitiveDataProtector sensitiveDataProtector) : IPrintersRepository
{
    private readonly AppDbContext _db = db;
    private readonly ISensitiveDataProtector _sensitiveDataProtector = sensitiveDataProtector;

    public async Task<List<Printer>> GetAllAsync(CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers.AsNoTracking().ToListAsync(ct);
        printers.ForEach(PopulateCredential);
        return printers;
    }

    public async Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers
            .AsNoTracking()
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Include(p => p.Location)
            .Include(p => p.ServiceState)
            .Include(p => p.BedType)
            .AsSplitQuery()
            .ToListAsync(ct);
        printers.ForEach(PopulateCredential);
        return printers;
    }

    public async Task<List<Printer>> GetAllForTemplateUpdateAsync(CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers
            .Include(p => p.Toolheads)
            .Include(p => p.ServiceState)
            .ToListAsync(ct);  // With tracking for updates
        foreach (Printer p in printers)
        {
            PopulateCredential(p);
        }

        return printers;
    }

    public async Task<Printer?> FindByIdForTemplateUpdateAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _db.Printers
            .Include(p => p.Toolheads)
            .Include(p => p.Cameras)
            .Include(p => p.ServiceState)
            .FirstOrDefaultAsync(p => p.Id == id, ct);  // With tracking for updates
        if (printer != null)
        {
            PopulateCredential(printer);
        }

        return printer;
    }

    public async Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _db.Printers.FindAsync(new object?[] { id }, ct);
        if (printer != null)
        {
            PopulateCredential(printer);
        }

        return printer;
    }

    public async Task<PrinterDispatchState?> FindDispatchStateAsync(
        Guid printerId,
        CancellationToken ct) =>
        await _db.PrinterDispatchStates.FindAsync([printerId], ct);

    public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct)
    {
        // AsNoTracking: all callers are read-only (details DTO, config read, status check)
        // No AsSplitQuery: single entity with few toolheads has no cartesian explosion risk,
        // and a single query avoids 5+ network round-trips to the database
        Printer? printer = await _db.Printers
            .AsNoTracking()
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Include(p => p.ServiceState).ThenInclude(s => s!.ObicoServer)
            .Include(p => p.Toolheads).ThenInclude(t => t.HotendModel)
            .Include(p => p.Toolheads).ThenInclude(t => t.ExtruderModel)
            .Include(p => p.Toolheads).ThenInclude(t => t.ToolheadModelDef)
            .Include(p => p.Toolheads).ThenInclude(t => t.NozzleModel)
            .Include(p => p.BedType)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (printer != null)
        {
            PopulateCredential(printer);
        }

        return printer;
    }

    public async Task AddAsync(Printer p, CancellationToken ct)
    {
        _ = _db.Printers.Add(p);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Printer p, CancellationToken ct)
    {
        // Ensure we have a tracked instance of the printer to remove (in case p is untracked)
        Printer? trackedPrinter = await FindByIdAsync(p.Id, ct);
        if (trackedPrinter == null)
        {
            // Printer doesn't exist, nothing to remove
            return;
        }

        // F4 — Wrap all compensating deletes + the parent removal in a single transaction so
        // a failure late in the sequence rolls back the earlier ExecuteDeleteAsync writes.
        // Cooperate with an existing outer transaction: only own the transaction if we
        // opened it. If a caller (e.g. DataImport Replace mode) has already begun a
        // transaction, we ride on theirs and let them decide commit/rollback.
        IDbContextTransaction? ownedTransaction = await BeginOwnedTransactionAsync(ct);
        try
        {
            // F2 + Dallas Fix 4 — Clear direct PartOutputMappings whose GcodeFileId points to
            // any GcodeFile SourcePrinter'd by this printer, BEFORE bulk-deleting those
            // GcodeFiles. The direct PartOutputMappings.GcodeFileId FK is Restrict (not
            // Cascade) after Dallas's full-chain adjudication for #953, so the bulk GcodeFile
            // delete below would otherwise FK-fail on any mapping still referencing a doomed
            // source GcodeFile. Mappings that reach the GcodeFile indirectly via
            // PrintProjectFileId are NOT touched here — they cascade normally when the
            // PrintProjectFile is deleted.
            await _db.PartOutputMappings
                .Where(m => m.GcodeFileId != null
                    && _db.GcodeFiles.Any(gf => gf.Id == m.GcodeFileId!.Value && gf.SourcePrinterId == trackedPrinter.Id))
                .ExecuteDeleteAsync(ct);

            // Clean up dependent records that have NoAction delete behavior to prevent FK
            // constraint violations. EF Core 10: use ExecuteDeleteAsync for efficient bulk
            // deletes without loading entities into memory.

            // Remove GcodeFile records that reference this printer as source
            await _db.GcodeFiles
                .Where(gf => gf.SourcePrinterId == trackedPrinter.Id)
                .ExecuteDeleteAsync(ct);

            // Remove PrintJob records assigned to this printer
            await _db.PrintJobs
                .Where(j => j.AssignedPrinterId == trackedPrinter.Id)
                .ExecuteDeleteAsync(ct);

            // Remove GcodeHarvestOperation records for this printer
            await _db.GcodeHarvestOperations
                .Where(h => h.PrinterId == trackedPrinter.Id)
                .ExecuteDeleteAsync(ct);

            // Remove MaintenanceLog records for this printer BEFORE alerts and schedules.
            // MaintenanceLog.PrinterId is Restrict (not Cascade), so logs must be deleted
            // explicitly before removing the printer. Logs are deleted first because they can
            // reference alerts via ResolvedAlertId (also Restrict).
            await _db.MaintenanceLogs
                .Where(l => l.PrinterId == trackedPrinter.Id)
                .ExecuteDeleteAsync(ct);

            // Remove MaintenanceAlert records for this printer BEFORE schedules and printer.
            // MaintenanceAlert.PrinterId is Restrict (not Cascade), so alerts must be deleted
            // explicitly before removing the printer.
            await _db.MaintenanceAlerts
                .Where(a => a.PrinterId == trackedPrinter.Id)
                .ExecuteDeleteAsync(ct);

            // Remove PrinterMaintenanceSchedule records for this printer BEFORE Printers.Remove.
            // The Schedule.PrinterId FK is Restrict (not Cascade) to break the SQL Server
            // multi-cascading-path graph Printers ⇒ Schedules ⇒ MaintenanceAlerts (SetNull)
            // that triggered error 1785 on fresh SQL Server InitialV1 (#953). Deleting schedules
            // here matches the surrounding cleanup pattern.
            await _db.PrinterMaintenanceSchedules
                .Where(s => s.PrinterId == trackedPrinter.Id)
                .ExecuteDeleteAsync(ct);

            await DeleteCalibrationProjectsAsync(trackedPrinter.Id, ct);

            // SpoolmanSpool references will be set to NULL by the database (SetNull behavior), so no need to handle them

            // Now remove the tracked printer
            _ = _db.Printers.Remove(trackedPrinter);
            _ = await _db.SaveChangesAsync(ct);

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(ct);
            }
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                try
                {
                    await ownedTransaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Rollback best-effort; original exception propagates.
                }
            }

            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Begins a new database transaction if the provider is relational AND no outer
    /// transaction is already in progress; otherwise returns null so we ride on the
    /// existing outer transaction (owned by the caller — e.g. DataImport Replace mode).
    /// SQLite in-memory tests that don't set up transactions return null and rely on the
    /// implicit per-SaveChanges transaction semantics of SQLite. Modeled on the
    /// <c>EfUserTaskRepository.BeginOwnedTransactionAsync</c> pattern already in this repo.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await _db.Database.BeginTransactionAsync(ct);
    }

    private async Task DeleteCalibrationProjectsAsync(Guid printerId, CancellationToken ct)
    {
        List<Guid> projectIds = await _db.CalibrationProjects
            .Where(project => project.PrinterId == printerId)
            .Select(project => project.Id)
            .ToListAsync(ct);
        if (projectIds.Count == 0)
        {
            return;
        }

        await _db.CalibrationOrchestrations
            .Where(orchestration => projectIds.Contains(orchestration.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationPhotos
            .Where(photo => projectIds.Contains(photo.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationObservations
            .Where(observation => projectIds.Contains(observation.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationAttemptEvents
            .Where(@event => projectIds.Contains(@event.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.GeneratedProfileRevisionOperations
            .Where(operation => _db.GeneratedProfileRevisions.Any(revision =>
                revision.Id == operation.GeneratedProfileRevisionId
                && projectIds.Contains(revision.ProjectId)))
            .ExecuteDeleteAsync(ct);
        await _db.GeneratedProfileRevisions
            .Where(revision => projectIds.Contains(revision.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationAttempts
            .Where(attempt => projectIds.Contains(attempt.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.PrinterConfigurationSnapshots
            .Where(snapshot => projectIds.Contains(snapshot.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationDrafts
            .Where(draft => projectIds.Contains(draft.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationIdempotencyRecords
            .Where(record => record.ProjectId != null && projectIds.Contains(record.ProjectId.Value))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationChanges
            .Where(change => projectIds.Contains(change.ProjectId))
            .ExecuteDeleteAsync(ct);
        await _db.CalibrationProjects
            .Where(project => projectIds.Contains(project.Id))
            .ExecuteDeleteAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);

    public void Detach(Printer p)
    {
        // Remove the entity from the tracker so it can be re-added without conflicts
        EntityEntry<Printer> entry = _db.Entry(p);
        if (entry != null && entry.State != EntityState.Detached)
        {
            entry.State = EntityState.Detached;
        }
    }

    public async Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct)
    {
        IQueryable<Printer> q = _db.Printers
            .AsNoTracking()
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Include(p => p.Location)
            .AsSplitQuery();
        if (ids != null && ids.Length > 0)
        {
            q = q.Where(p => ids.Contains(p.Id));
        }

        List<Printer> printers = await q.ToListAsync(ct);
        printers.ForEach(PopulateCredential);
        return printers;
    }

    public async Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct)
    {
        return await _db.Printers.AnyAsync(p => p.Name == name || p.ServerUrl == serverUrl, ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        return await _db.Printers.CountAsync(ct);
    }

    public async Task<List<Printer>> GetByBackendAsync(PrinterBackend backend, CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers
            .AsNoTracking()
            .Where(p => p.Backend == (int)backend)
            .ToListAsync(ct);
        printers.ForEach(PopulateCredential);
        return printers;
    }

    public async Task<List<Printer>> GetByBackendWithToolheadsAsync(PrinterBackend backend, CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers
            .AsNoTracking()
            .Include(p => p.Toolheads)
            .Where(p => p.Backend == (int)backend)
            .ToListAsync(ct);
        printers.ForEach(PopulateCredential);
        return printers;
    }

    /// <summary>
    /// Finds a printer by its ServerUrl efficiently using a direct database query.
    /// This is much more efficient than loading all printers into memory.
    /// </summary>
    /// <param name="serverUrl">The server URL to search for.</param>
    /// <param name="ct">Cancellation token for the async operation.</param>
    public async Task<Printer?> FindByServerUrlAsync(string serverUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return null;
        }

        // Normalize the URL for comparison (strip trailing slashes)
        string normalizedUrl = serverUrl.TrimEnd('/');

        // Query directly by ServerUrl - much more efficient than GetAllAsync + FirstOrDefault
        Printer? printer = await _db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ServerUrl == normalizedUrl || p.ServerUrl == serverUrl, ct);

        if (printer != null)
        {
            PopulateCredential(printer);
        }

        return printer;
    }

    /// <summary>
    /// Detaches all tracked entities from the DbContext to prevent "second operation" errors
    /// in loops where multiple operations are performed on the same context.
    /// This clears the change tracker without persisting any uncommitted changes.
    /// </summary>
    public void DetachAllEntities()
    {
        foreach (EntityEntry? entry in _db.ChangeTracker.Entries().ToList())
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    /// <summary>
    /// Populates the transient Credential property on a printer entity.
    /// Creates a PrinterCredential with ApiKey, Username, and Password as applicable.
    /// Backend clients can then use whatever properties they need for authentication.
    /// Also decrypts Password and ApiKey fields on the entity for editing scenarios.
    /// </summary>
    private void PopulateCredential(Printer p)
    {
        // Decrypt sensitive fields directly on the entity for editing/display
        p.Password = DecryptIfNeeded(p.Password);
        p.ApiKey = DecryptIfNeeded(p.ApiKey);

        // Build the PrinterCredential with all available auth properties
        p.Credential = PrinterCredential.FromAll(p.ApiKey, p.Username, p.Password);
    }

    /// <summary>
    /// Decrypts a potentially encrypted value. Returns the original value if decryption fails
    /// (e.g., if the data is already in plaintext for backward compatibility).
    /// </summary>
    private string? DecryptIfNeeded(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Skip decryption attempt for values that are clearly not encrypted.
        // This avoids expensive CryptographicException throws for plaintext credentials.
        if (!IsLikelyEncrypted(value))
        {
            return value;
        }

        // Try to decrypt - if it fails, the data might be plaintext (migration scenario)
        string? decrypted = _sensitiveDataProtector.Unprotect(value);

        // If decryption returned null, assume the data is plaintext
        return decrypted ?? value;
    }

    /// <summary>
    /// Encrypts sensitive fields (ApiKey, Password) on all tracked Printer entities
    /// that are being added or modified. Call this before SaveChangesAsync.
    /// This is the encryption counterpart to PopulateCredential/DecryptIfNeeded.
    /// </summary>
    public void EncryptSensitiveFieldsOnTrackedEntities()
    {
        // Find all tracked Printer entities that have been Added or Modified
        var trackedPrinters = _db.ChangeTracker.Entries<Printer>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        foreach (Printer printer in trackedPrinters)
        {
            // Encrypt ApiKey if present and not already encrypted
            printer.ApiKey = EncryptIfNeeded(printer.ApiKey);

            // Encrypt Password if present and not already encrypted
            printer.Password = EncryptIfNeeded(printer.Password);
        }
    }

    /// <summary>
    /// Encrypts a value if it's not null/empty and not already encrypted.
    /// This is the encryption counterpart to DecryptIfNeeded.
    /// </summary>
    private string? EncryptIfNeeded(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Check if already encrypted - Data Protection API base64 values start with "CfDJ" and are typically long
        if (IsLikelyEncrypted(value))
        {
            return value;
        }

        // Encrypt the plaintext value
        return _sensitiveDataProtector.Protect(value);
    }

    /// <summary>
    /// Heuristically determines if a value is already encrypted.
    /// Data Protection API encrypted values are base64 strings that start with "CfDJ" and are typically 100+ chars.
    /// </summary>
    private static bool IsLikelyEncrypted(string value)
    {
        // Data Protection API encrypted strings are base64 encoded and start with "CfDJ"
        // They are typically 100+ characters long
        return value.Length > 100 && value.StartsWith("CfDJ", StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public async Task<Printer?> FindByCurrentSpoolIdAsync(int spoolId, CancellationToken ct)
    {
        return await _db.Printers
            .FirstOrDefaultAsync(p => p.CurrentSpoolId == spoolId, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct)
    {
        return await _db.Printers.AnyAsync(p => p.Id == id, ct);
    }

    /// <inheritdoc/>
    public async Task<Printer?> FindByIdWithToolheadsAsync(Guid id, CancellationToken ct)
    {
        return await _db.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void AddToolheads(IEnumerable<Toolhead> toolheads)
    {
        _db.Set<Toolhead>().AddRange(toolheads);
    }
}
