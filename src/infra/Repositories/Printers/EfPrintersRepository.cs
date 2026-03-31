using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
        List<Printer> printers = await _db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).Include(p => p.Location).AsSplitQuery().ToListAsync(ct);
        printers.ForEach(PopulateCredential);
        return printers;
    }

    public async Task<List<Printer>> GetAllForTemplateUpdateAsync(CancellationToken ct)
    {
        List<Printer> printers = await _db.Printers.Include(p => p.Toolheads).ToListAsync(ct);  // With tracking for updates
        foreach (Printer p in printers)
        {
            PopulateCredential(p);
        }

        return printers;
    }

    public async Task<Printer?> FindByIdForTemplateUpdateAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _db.Printers.Include(p => p.Toolheads).Include(p => p.Cameras).FirstOrDefaultAsync(p => p.Id == id, ct);  // With tracking for updates
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

    public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct)
    {
        Printer? printer = await _db.Printers
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Include(p => p.ObicoServer)
            .Include(p => p.Toolheads).ThenInclude(t => t.HotendModel)
            .Include(p => p.Toolheads).ThenInclude(t => t.ExtruderModel)
            .Include(p => p.Toolheads).ThenInclude(t => t.ToolheadModelDef)
            .Include(p => p.Toolheads).ThenInclude(t => t.NozzleModel)
            .AsSplitQuery()
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

        // Clean up dependent records that have NoAction delete behavior to prevent FK constraint violations
        // EF Core 10: Use ExecuteDeleteAsync for efficient bulk deletes without loading entities into memory

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

        // SpoolmanSpool references will be set to NULL by the database (SetNull behavior), so no need to handle them

        // Now remove the tracked printer
        _ = _db.Printers.Remove(trackedPrinter);
        _ = await _db.SaveChangesAsync(ct);
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
