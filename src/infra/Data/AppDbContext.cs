using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSettingsEntity> AppSettingsEntities => Set<AppSettingsEntity>();

    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();

    public DbSet<Printer> Printers => Set<Printer>();

    public DbSet<PrinterGroup> PrinterGroups => Set<PrinterGroup>();

    public DbSet<PrinterGroupAccess> PrinterGroupAccesses => Set<PrinterGroupAccess>();

    public DbSet<BedType> BedTypes => Set<BedType>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Spool> Spools => Set<Spool>();

    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();

    public DbSet<PrinterModel> PrinterModels => Set<PrinterModel>();

    public DbSet<PrinterModelAlias> PrinterModelAliases => Set<PrinterModelAlias>();

    public DbSet<PrinterModelToolhead> PrinterModelToolheads => Set<PrinterModelToolhead>();

    public DbSet<FilamentType> FilamentTypes => Set<FilamentType>();

    public DbSet<SpoolmanConfig> SpoolmanConfigs => Set<SpoolmanConfig>();

    public DbSet<BarcodeScanLog> BarcodeScanLogs => Set<BarcodeScanLog>();

    // Tags
    public DbSet<Tag> Tags => Set<Tag>();

    // G-code Library & Job Queue
    public DbSet<GcodeFile> GcodeFiles => Set<GcodeFile>();

    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();

    // Per-toolhead filament usage (multi-tool/MMU jobs)
    public DbSet<PrintJobToolheadUsage> PrintJobToolheadUsages => Set<PrintJobToolheadUsage>();

    // Print Projects (multi-file job tracking)
    public DbSet<PrintProject> PrintProjects => Set<PrintProject>();

    public DbSet<PrintProjectFile> PrintProjectFiles => Set<PrintProjectFile>();

    // Print Project Templates
    public DbSet<PrintProjectTemplate> PrintProjectTemplates => Set<PrintProjectTemplate>();

    public DbSet<PrintProjectTemplateFile> PrintProjectTemplateFiles => Set<PrintProjectTemplateFile>();

    public DbSet<JobStateHistory> JobStateHistories => Set<JobStateHistory>();

    public DbSet<JobSchedule> JobSchedules => Set<JobSchedule>();

    public DbSet<JobExecution> JobExecutions => Set<JobExecution>();

    public DbSet<PrintJobStatistics> PrintJobStatistics => Set<PrintJobStatistics>();

    public DbSet<RetryPolicy> RetryPolicies => Set<RetryPolicy>();

    public DbSet<JobRetry> JobRetries => Set<JobRetry>();

    public DbSet<DispatchLog> DispatchLogs => Set<DispatchLog>();

    public DbSet<DispatchSettings> DispatchSettings => Set<DispatchSettings>();

    public DbSet<Toolhead> Toolheads => Set<Toolhead>();

    public DbSet<PrinterDispatchState> PrinterDispatchStates => Set<PrinterDispatchState>();

    public DbSet<PrinterServiceState> PrinterServiceStates => Set<PrinterServiceState>();

    public DbSet<GcodeHarvestOperation> GcodeHarvestOperations => Set<GcodeHarvestOperation>();

    public DbSet<HarvestDiscoveredFile> HarvestDiscoveredFiles => Set<HarvestDiscoveredFile>();

    public DbSet<HarvestFileGcodeFileMapping> HarvestFileGcodeFileMappings => Set<HarvestFileGcodeFileMapping>();

    public DbSet<GcodeHarvestQueueItem> GcodeHarvestQueueItems => Set<GcodeHarvestQueueItem>();

    public DbSet<UserTask> UserTasks => Set<UserTask>();

    // Print approvals (pending Upload+Print approvals)
    public DbSet<PrintApproval> PrintApprovals => Set<PrintApproval>();

    // File Health & Consistency Auditing
    public DbSet<FileHealthAudit> FileHealthAudits => Set<FileHealthAudit>();

    // Notifications & User Communication
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    // User Management & Authentication
    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<UserAction> UserActions => Set<UserAction>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<PasswordPolicyEntity> PasswordPolicies => Set<PasswordPolicyEntity>();

    public DbSet<FailedLoginAttempt> FailedLoginAttempts => Set<FailedLoginAttempt>();

    public DbSet<AuthAuditLog> AuthAuditLogs => Set<AuthAuditLog>();

    // Login attempt audit log (focused admin-facing security view)
    public DbSet<LoginAuditEntry> LoginAuditEntries => Set<LoginAuditEntry>();

    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

    // WebAuthn/FIDO2 passkey credentials
    public DbSet<UserPasskeyCredential> UserPasskeyCredentials => Set<UserPasskeyCredential>();

    // API Keys for OctoPrint API
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Component Model Definitions (extensible manufacturer-backed components)
    public DbSet<HotendModelDefinition> HotendModelDefinitions => Set<HotendModelDefinition>();

    public DbSet<ExtruderModelDefinition> ExtruderModelDefinitions => Set<ExtruderModelDefinition>();

    public DbSet<ToolheadModelDefinition> ToolheadModelDefinitions => Set<ToolheadModelDefinition>();

    public DbSet<NozzleModelDefinition> NozzleModelDefinitions => Set<NozzleModelDefinition>();

    // Printer Maintenance Module
    public DbSet<PrinterStatistics> PrinterStatisticsSet => Set<PrinterStatistics>();

    public DbSet<MaintenanceLog> MaintenanceLogs => Set<MaintenanceLog>();

    public DbSet<MaintenanceAlert> MaintenanceAlerts => Set<MaintenanceAlert>();

    // Maintenance Plans (hierarchical: Plan → Task → Component)
    public DbSet<MaintenancePlan> MaintenancePlans => Set<MaintenancePlan>();

    public DbSet<MaintenanceTask> MaintenanceTasks => Set<MaintenanceTask>();

    public DbSet<MaintenanceComponent> MaintenanceComponents => Set<MaintenanceComponent>();

    public DbSet<MaintenanceTaskComponent> MaintenanceTaskComponents => Set<MaintenanceTaskComponent>();

    public DbSet<PlanTask> PlanTasks => Set<PlanTask>();

    public DbSet<PrinterMaintenanceSchedule> PrinterMaintenanceSchedules => Set<PrinterMaintenanceSchedule>();

    // Cameras (standalone webcams not attached to printers)
    public DbSet<Camera> Cameras => Set<Camera>();

    // Camera snapshots (captured on print events)
    public DbSet<CameraSnapshot> CameraSnapshots => Set<CameraSnapshot>();

    // NFC Devices (ESP32 + PN532 filament spool readers)
    public DbSet<NfcDevice> NfcDevices => Set<NfcDevice>();

    public DbSet<NfcScanEvent> NfcScanEvents => Set<NfcScanEvent>();

    public DbSet<NfcTagBinding> NfcTagBindings => Set<NfcTagBinding>();

    // Webhooks
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDeliveryLog> WebhookDeliveryLogs => Set<WebhookDeliveryLog>();

    // Obico ML Servers
    public DbSet<ObicoServer> ObicoServers => Set<ObicoServer>();

    // Failure-detection incident history
    public DbSet<FailureDetectionIncident> FailureDetectionIncidents => Set<FailureDetectionIncident>();

    // Catalog version tracking for update detection
    public DbSet<CatalogVersion> CatalogVersions => Set<CatalogVersion>();

    // Material equivalence clusters for auto-matching
    public DbSet<MaterialCluster> MaterialClusters => Set<MaterialCluster>();

    public DbSet<MaterialClusterMember> MaterialClusterMembers => Set<MaterialClusterMember>();

    // Print quotas and user balances
    public DbSet<PrintQuota> PrintQuotas => Set<PrintQuota>();

    public DbSet<UserBalance> UserBalances => Set<UserBalance>();

    public DbSet<BalanceTransaction> BalanceTransactions => Set<BalanceTransaction>();

    // Quota group memberships (user ↔ named group associations)
    public DbSet<UserQuotaGroupMembership> UserQuotaGroupMemberships => Set<UserQuotaGroupMembership>();

    // Custom fields (extensible metadata for Printers and Users)
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();

    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();

    // Electricity Monitoring (power monitors + time-series readings)
    public DbSet<PowerMonitor> PowerMonitors => Set<PowerMonitor>();

    public DbSet<PowerReading> PowerReadings => Set<PowerReading>();

    // Per-user settings (theme, locale, slicer defaults, etc.)
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    // Printed-part inventory (see #714). Distinct from MaintenanceComponents which
    // are consumed by printer maintenance, not produced by prints.
    public DbSet<PartInventory> PartInventories => Set<PartInventory>();

    public DbSet<Bin> Bins => Set<Bin>();

    public DbSet<PartInventoryAdjustment> PartInventoryAdjustments => Set<PartInventoryAdjustment>();

    public DbSet<PartOutputMapping> PartOutputMappings => Set<PartOutputMapping>();

    public DbSet<PrintJobPartOutputSnapshot> PrintJobPartOutputSnapshots => Set<PrintJobPartOutputSnapshot>();

    public DbSet<PartHarvestOutputSnapshot> PartHarvestOutputSnapshots => Set<PartHarvestOutputSnapshot>();

    // Per-user snoozes for the unified attention feed (issue #707).
    public DbSet<AttentionSnooze> AttentionSnoozes => Set<AttentionSnooze>();

    // Durable audit of guided filament-swap material-mismatch overrides (issue #710).
    public DbSet<FilamentSwapOverride> FilamentSwapOverrides => Set<FilamentSwapOverride>();

    // Persistent Idempotency-Key records for offline write-replay (issue #715).
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Apply all IEntityTypeConfiguration classes from this assembly
        // This enables separation of entity configurations into individual files
        // in the Data/Configurations folder for better maintainability
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Fix E/I (issue #713): the shift-plan compiler dedupe index. A UNIQUE
        // filtered index on (SourceKind, SourceId) restricted to OPEN rows
        // (SourceId IS NOT NULL AND Status IN (Pending=0, InProgress=1)) guarantees
        // at most one open compiler task per source even if two compiler ticks race,
        // and covers GetOpenBySourceAsync's (SourceKind, SourceId, Status) lookup.
        // The filter SQL is provider-specific, so it must be declared here rather
        // than in UserTaskConfiguration. Providers that don't support filtered
        // indexes fall back to a plain non-unique index (best-effort dedupe).
        Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder<UserTask> sourceDedupeIndex =
            modelBuilder.Entity<UserTask>()
                .HasIndex(t => new { t.SourceKind, t.SourceId })
                .HasDatabaseName("IX_UserTasks_SourceKind_SourceId");

        switch (Database.ProviderName)
        {
            case "Npgsql.EntityFrameworkCore.PostgreSQL":
            case "Microsoft.EntityFrameworkCore.Sqlite":
                _ = sourceDedupeIndex.IsUnique().HasFilter("\"SourceId\" IS NOT NULL AND \"Status\" IN (0, 1)");
                break;
            case "Microsoft.EntityFrameworkCore.SqlServer":
                _ = sourceDedupeIndex.IsUnique().HasFilter("[SourceId] IS NOT NULL AND [Status] IN (0, 1)");
                break;
            default:
                // MySQL / InMemory / others: no filtered-index support — leave non-unique.
                break;
        }

        // Profile-import tasks are globally aggregated by printer model; UserTask has no
        // per-task UserId. This filtered unique index prevents concurrent recovery of the
        // same open PrinterModel ProfileImport task while retaining terminal task history
        // and leaving legacy/generic ProfileImport rows unaffected.
        Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder<UserTask> profileImportRecoveryIndex =
            modelBuilder.Entity<UserTask>()
                .HasIndex(t => new { t.TaskType, t.EntityType, t.EntityId })
                .HasDatabaseName("IX_UserTasks_OpenProfileImport");

        switch (Database.ProviderName)
        {
            case "Npgsql.EntityFrameworkCore.PostgreSQL":
            case "Microsoft.EntityFrameworkCore.Sqlite":
                _ = profileImportRecoveryIndex.IsUnique().HasFilter(
                    "\"TaskType\" = 1 AND \"EntityType\" = 'PrinterModel' AND \"Status\" IN (0, 1)");
                break;
            case "Microsoft.EntityFrameworkCore.SqlServer":
                _ = profileImportRecoveryIndex.IsUnique().HasFilter(
                    "[TaskType] = 1 AND [EntityType] = 'PrinterModel' AND [Status] IN (0, 1)");
                break;
            default:
                // MySQL / InMemory / others: no filtered-index support — leave non-unique.
                break;
        }

        // SQLite does not support DateTimeOffset natively in ORDER BY / WHERE clauses.
        // Apply a transparent UTC DateTime conversion so all DateTimeOffset properties
        // on LoginAuditEntry round-trip correctly through the SQLite text store.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            // SQLite has no native DateTimeOffset type. We normalize to UTC for storage
            // since LoginAuditService always writes DateTimeOffset.UtcNow. This conversion
            // is LOSSY for non-UTC offsets — that scenario is forbidden by service contract.
            _ = modelBuilder.Entity<LoginAuditEntry>()
                .Property(e => e.Timestamp)
                .HasConversion(
                    v => v.UtcDateTime,
                    v => new DateTimeOffset(v, TimeSpan.Zero));
        }

        // Seed default password policy if table empty (idempotent for EnsureCreated)
        if (Database.ProviderName != null)
        {
            // Use a static value for UpdatedAt to avoid model instability in migrations
            _ = modelBuilder.Entity<PasswordPolicyEntity>().HasData(new PasswordPolicyEntity
            {
                Id = 1,
                MinLength = 8,
                RequireUppercase = false,
                RequireLowercase = false,
                RequireDigit = false,
                RequireSymbol = false,
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsurePartInventoryLedgerIsAppendOnly();
        EnsureInventoryIdentitiesAreImmutable();
        PopulateCaseInsensitiveShadowColumns();
        StampRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsurePartInventoryLedgerIsAppendOnly();
        EnsureInventoryIdentitiesAreImmutable();
        PopulateCaseInsensitiveShadowColumns();
        StampRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsurePartInventoryLedgerIsAppendOnly()
    {
        bool mutationRequested = ChangeTracker.Entries<PartInventoryAdjustment>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (mutationRequested)
        {
            throw new InvalidOperationException(
                "Printed-part inventory adjustments are immutable; append a correcting adjustment instead.");
        }

        bool outputSnapshotMutationRequested = ChangeTracker.Entries<PrintJobPartOutputSnapshot>()
            .Any(entry => entry.State == EntityState.Modified
                || (entry.State == EntityState.Deleted && !IsParentJobDeleted(entry.Entity.PrintJobId)));
        bool harvestSnapshotMutationRequested = ChangeTracker.Entries<PartHarvestOutputSnapshot>()
            .Any(entry => entry.State == EntityState.Modified
                || (entry.State == EntityState.Deleted && !IsParentJobDeleted(entry.Entity.PrintJobId)));
        if (outputSnapshotMutationRequested || harvestSnapshotMutationRequested)
        {
            throw new InvalidOperationException(
                "Printed-part output snapshots are immutable; append a new harvest instead.");
        }
    }

    private bool IsParentJobDeleted(Guid printJobId)
        => ChangeTracker.Entries<PrintJob>()
            .Any(entry => entry.Entity.Id == printJobId && entry.State == EntityState.Deleted);

    private void EnsureInventoryIdentitiesAreImmutable()
    {
        bool skuMutationRequested = ChangeTracker.Entries<PartInventory>()
            .Any(entry => entry.State == EntityState.Modified
                && entry.Property(part => part.Sku).IsModified);
        bool binCodeMutationRequested = ChangeTracker.Entries<Bin>()
            .Any(entry => entry.State == EntityState.Modified
                && entry.Property(bin => bin.Code).IsModified);
        if (skuMutationRequested || binCodeMutationRequested)
        {
            throw new InvalidOperationException(
                "Printed-part SKU and bin-code identities are immutable; create a new identity instead.");
        }
    }

    private void PopulateCaseInsensitiveShadowColumns()
    {
        foreach (EntityEntry<Manufacturer> entry in ChangeTracker.Entries<Manufacturer>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                string name = entry.Entity.Name ?? string.Empty;
                entry.Property("NameLowered").CurrentValue = name.ToLowerInvariant();
            }
        }

        foreach (EntityEntry<PrinterModel> entry in ChangeTracker.Entries<PrinterModel>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                string name = entry.Entity.Name ?? string.Empty;
                entry.Property("NameLowered").CurrentValue = name.ToLowerInvariant();

                // Always bump UpdatedAt so catalog update detection picks up the change
                if (entry.State == EntityState.Modified)
                {
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                }
            }
        }
    }

    private void StampRowVersions()
    {
        byte[] newVersion = Guid.NewGuid().ToByteArray();

        foreach (EntityEntry<UserSettings> entry in ChangeTracker.Entries<UserSettings>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = newVersion;
            }
        }

        foreach (EntityEntry<AppSettingsEntity> entry in ChangeTracker.Entries<AppSettingsEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = newVersion;
            }
        }
    }
}
