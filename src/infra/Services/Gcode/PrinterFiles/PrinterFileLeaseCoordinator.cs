using System.Data;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Gcode.PrinterFiles;

/// <summary>Database-fenced, renewable feature ownership across API replicas.</summary>
public sealed class PrinterFileLeaseCoordinator(
    IDbContextFactory<AppDbContext> dbFactory,
    IPrinterStatusCacheReader statusCache,
    ILogger<PrinterFileLeaseCoordinator> logger) : IPrinterFileLeaseCoordinator
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);

    public async Task<IPrinterFileLease?> TryAcquireAsync(
        Guid printerId, bool interactive, CancellationToken ct = default)
    {
        await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            DateTime now = DateTime.UtcNow;
            var settings = await PrinterFileIndexPolicy.ReadAsync(db, ct);
            Printer? printer = await db.Printers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == printerId, ct);
            if (printer is null || !printer.IsEnabled)
            {
                return null;
            }

            if (!await db.AppSettingsEntities.AnyAsync(x => x.Key == PrinterFileIndexSettings.SectionName, ct))
            {
                db.AppSettingsEntities.Add(new AppSettingsEntity
                {
                    Key = PrinterFileIndexSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(settings),
                    UpdatedAt = now,
                });
            }

            string fingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer);
            PrinterFileScanState? state = await db.PrinterFileScanStates
                .SingleOrDefaultAsync(x => x.SourcePrinterId == printerId, ct);
            if (state is null)
            {
                state = new PrinterFileScanState
                {
                    Id = printerId,
                    SourcePrinterId = printerId,
                    RegisteredPrinterId = printerId,
                    ConfigurationFingerprint = fingerprint,
                    NextScanAtUtc = now + PrinterFileIndexPolicy.InitialJitter(printerId, settings),
                };
                db.PrinterFileScanStates.Add(state);
            }

            if (state.ConfigurationFingerprint != fingerprint)
            {
                state.ConfigurationFingerprint = fingerprint;
                state.ConfigurationGeneration++;
                state.ConsecutiveFailures = 0;
                state.Outcome = PrinterFileScanOutcome.NeverScanned;
                state.NextScanAtUtc = now;
            }

            if (interactive)
            {
                state.InteractiveRequestedAtUtc = now;
            }

            bool occupied = state.LeaseOwner.HasValue && state.LeaseExpiresAtUtc > now;
            bool confirmedIdle = interactive || PrinterFileIndexPolicy.IsConfirmedIdle(statusCache.GetSnapshot(printerId), now);
            bool deferred = !interactive && (!settings.Enabled || !state.Enabled ||
                state.NextScanAtUtc > now ||
                state.InteractiveRequestedAtUtc > now.AddMinutes(-2) ||
                !confirmedIdle);
            if (occupied || deferred)
            {
                if (!occupied && !interactive && settings.Enabled && state.Enabled &&
                    state.NextScanAtUtc <= now && !confirmedIdle)
                {
                    state.NextScanAtUtc = now + TimeSpan.FromSeconds(30) +
                        PrinterFileIndexPolicy.InitialJitter(printerId, settings);
                    state.ReasonCode = "waiting_for_confirmed_idle";
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            }

            List<PrinterFileIoSlot> slots = await db.PrinterFileIoSlots.OrderBy(x => x.Id).ToListAsync(ct);
            PrinterFileIoSlot? slot = null;
            int occupiedSlots = slots.Count(x => x.LeaseOwner.HasValue && x.LeaseExpiresAtUtc > now);
            for (int number = 1; occupiedSlots < settings.MaxConcurrentIo && number <= settings.MaxConcurrentIo; number++)
            {
                PrinterFileIoSlot? candidate = slots.SingleOrDefault(x => x.Id == number);
                if (candidate is null)
                {
                    candidate = new PrinterFileIoSlot { Id = number };
                    db.PrinterFileIoSlots.Add(candidate);
                }

                if (!candidate.LeaseOwner.HasValue || candidate.LeaseExpiresAtUtc <= now)
                {
                    slot = candidate;
                    break;
                }
            }

            if (slot is null)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            }

            Guid owner = Guid.NewGuid();
            state.LeaseOwner = owner;
            state.LeaseExpiresAtUtc = now + LeaseDuration;
            state.LeaseFence++;
            state.InteractiveRequestedAtUtc = null;
            slot.LeaseOwner = owner;
            slot.SourcePrinterId = printerId;
            slot.LeaseExpiresAtUtc = state.LeaseExpiresAtUtc;
            slot.LeaseFence++;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new Lease(dbFactory, statusCache, logger, state, slot, fingerprint, interactive, ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogDebug(ex, "Printer file lease acquisition raced for {PrinterId}", printerId);
            return null;
        }
        catch (DbUpdateException ex) when (PartInventoryService.IsUniqueViolation(ex))
        {
            logger.LogDebug(ex, "Printer file lease enrollment raced for {PrinterId}", printerId);
            return null;
        }
    }

    private sealed class Lease : IPrinterFileLease
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly ILogger _logger;
        private readonly IPrinterStatusCacheReader _statusCache;
        private readonly CancellationTokenSource _lifetime;
        private readonly Task _renewal;
        private readonly int _slotId;
        private readonly long _slotFence;
        private readonly string _fingerprint;
        private readonly bool _interactive;
        private int _disposed;

        public Lease(
            IDbContextFactory<AppDbContext> dbFactory,
            IPrinterStatusCacheReader statusCache,
            ILogger logger,
            PrinterFileScanState state,
            PrinterFileIoSlot slot,
            string fingerprint,
            bool interactive,
            CancellationToken ct)
        {
            _dbFactory = dbFactory;
            _statusCache = statusCache;
            _logger = logger;
            _slotId = slot.Id;
            _slotFence = slot.LeaseFence;
            _fingerprint = fingerprint;
            _interactive = interactive;
            PrinterId = state.SourcePrinterId;
            OwnerId = state.LeaseOwner!.Value;
            Fence = state.LeaseFence;
            ConfigurationGeneration = state.ConfigurationGeneration;
            MutationGeneration = state.MutationGeneration;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _renewal = RenewAsync();
        }

        public Guid PrinterId { get; }

        public Guid OwnerId { get; }

        public long Fence { get; }

        public long ConfigurationGeneration { get; }

        public long MutationGeneration { get; }

        public CancellationToken Token => _lifetime.Token;

        public async Task<bool> IsCurrentAsync(CancellationToken ct = default)
        {
            if (Token.IsCancellationRequested)
            {
                return false;
            }

            await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct);
            return await ReadCurrentAsync(db, ct) is not null;
        }

        public async Task<bool> FencePublicationAsync(AppDbContext db, CancellationToken ct = default)
        {
            if (db.Database.CurrentTransaction is null)
            {
                throw new InvalidOperationException("Printer file publication requires an explicit transaction.");
            }

            AppSettingsEntity? policy = await db.AppSettingsEntities.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Key == PrinterFileIndexSettings.SectionName, ct);
            if (policy is null)
            {
                return false;
            }

            var current = await ReadCurrentAsync(db, ct);
            if (Token.IsCancellationRequested || current is null)
            {
                return false;
            }

            (Printer printer, PrinterFileScanState state, PrinterFileIoSlot slot) = current.Value;
            int policyWrites = await db.AppSettingsEntities.Where(x => x.Id == policy.Id && x.Revision == policy.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision), ct);

            // Write all policy/ownership barriers in the caller's publication transaction.
            int printerWrites = await db.Printers.Where(x => x.Id == PrinterId && x.Revision == printer.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision), ct);
            int stateWrites = await db.PrinterFileScanStates.Where(x => x.Id == state.Id &&
                x.Revision == state.Revision && x.LeaseOwner == OwnerId && x.LeaseFence == Fence)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            int slotWrites = await db.PrinterFileIoSlots.Where(x => x.Id == _slotId &&
                x.Revision == slot.Revision && x.LeaseOwner == OwnerId && x.LeaseFence == _slotFence)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, x => x.Revision + 1), ct);
            return policyWrites == 1 && printerWrites == 1 && stateWrites == 1 && slotWrites == 1;
        }

        private async Task<(Printer Printer, PrinterFileScanState State, PrinterFileIoSlot Slot)?> ReadCurrentAsync(
            AppDbContext db, CancellationToken ct)
        {
            DateTime now = DateTime.UtcNow;
            var settings = await PrinterFileIndexPolicy.ReadAsync(db, ct);
            Printer? printer = await db.Printers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == PrinterId, ct);
            PrinterFileScanState? state = await db.PrinterFileScanStates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.SourcePrinterId == PrinterId, ct);
            PrinterFileIoSlot? slot = await db.PrinterFileIoSlots.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == _slotId, ct);
            if (printer is null || !printer.IsEnabled ||
                PrinterFileIndexPolicy.ConfigurationFingerprint(printer) != _fingerprint ||
                state is null || state.RegisteredPrinterId != PrinterId ||
                state.ConfigurationGeneration != ConfigurationGeneration ||
                state.MutationGeneration != MutationGeneration ||
                state.LeaseOwner != OwnerId || state.LeaseFence != Fence ||
                !state.LeaseExpiresAtUtc.HasValue || state.LeaseExpiresAtUtc <= now ||
                slot is null || slot.LeaseOwner != OwnerId || slot.LeaseFence != _slotFence ||
                !slot.LeaseExpiresAtUtc.HasValue || slot.LeaseExpiresAtUtc <= now ||
                (!_interactive && (!settings.Enabled || !state.Enabled ||
                 !PrinterFileIndexPolicy.IsConfirmedIdle(_statusCache.GetSnapshot(PrinterId), now))))
            {
                return null;
            }

            return (printer, state, slot);
        }

        private async Task RenewAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (await timer.WaitForNextTickAsync(Token))
                {
                    await using AppDbContext db = await _dbFactory.CreateDbContextAsync(Token);
                    await using var transaction = await db.Database.BeginTransactionAsync(Token);
                    if (!await FencePublicationAsync(db, Token))
                    {
                        await _lifetime.CancelAsync();
                        return;
                    }

                    DateTime expires = DateTime.UtcNow + LeaseDuration;
                    await db.PrinterFileScanStates.Where(x => x.SourcePrinterId == PrinterId &&
                        x.LeaseOwner == OwnerId && x.LeaseFence == Fence)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAtUtc, expires), Token);
                    await db.PrinterFileIoSlots.Where(x => x.Id == _slotId &&
                        x.LeaseOwner == OwnerId && x.LeaseFence == _slotFence)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAtUtc, expires), Token);
                    await transaction.CommitAsync(Token);
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Printer file lease renewal failed for {PrinterId}", PrinterId);
                await _lifetime.CancelAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _lifetime.CancelAsync();
#pragma warning disable VSTHRD003 // This owned background task has no foreground or synchronization-context dependency.
            try
            {
                await _renewal;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Printer file lease renewal stopped unexpectedly for {PrinterId}", PrinterId);
            }
#pragma warning restore VSTHRD003
            try
            {
                await using AppDbContext db = await _dbFactory.CreateDbContextAsync();
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.PrinterFileScanStates.Where(x => x.SourcePrinterId == PrinterId &&
                    x.LeaseOwner == OwnerId && x.LeaseFence == Fence)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, (Guid?)null)
                        .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(x => x.Revision, x => x.Revision + 1));
                await db.PrinterFileIoSlots.Where(x => x.Id == _slotId &&
                    x.LeaseOwner == OwnerId && x.LeaseFence == _slotFence)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, (Guid?)null)
                        .SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(x => x.Revision, x => x.Revision + 1));
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Printer file lease release failed for {PrinterId}; expiry will release ownership", PrinterId);
            }
            finally
            {
                _lifetime.Dispose();
            }
        }
    }
}
