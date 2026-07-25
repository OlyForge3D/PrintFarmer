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

    // Model collections (user-owned groupings of 3D models; sync epic #835)
    public DbSet<ModelCollection> ModelCollections => Set<ModelCollection>();

    public DbSet<ModelCollectionMembership> ModelCollectionMemberships => Set<ModelCollectionMembership>();

    // Library sync change journal with tombstones (sync epic #835, issue #844)
    public DbSet<Farm.Infrastructure.Domain.Sync.LibrarySyncChange> LibrarySyncChanges => Set<Farm.Infrastructure.Domain.Sync.LibrarySyncChange>();

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

    // Calibration is an AppDbContext bounded context. Soft identifiers are used for
    // separately deployed slicer and storage services; no cross-context FK is modeled.
    public DbSet<CalibrationProject> CalibrationProjects => Set<CalibrationProject>();

    public DbSet<PrinterConfigurationSnapshot> PrinterConfigurationSnapshots =>
        Set<PrinterConfigurationSnapshot>();

    public DbSet<CalibrationDraft> CalibrationDrafts => Set<CalibrationDraft>();

    public DbSet<CalibrationAttempt> CalibrationAttempts => Set<CalibrationAttempt>();

    public DbSet<CalibrationAttemptEvent> CalibrationAttemptEvents => Set<CalibrationAttemptEvent>();

    public DbSet<CalibrationObservation> CalibrationObservations => Set<CalibrationObservation>();

    public DbSet<CalibrationPhoto> CalibrationPhotos => Set<CalibrationPhoto>();

    public DbSet<CalibrationBlobCleanup> CalibrationBlobCleanups => Set<CalibrationBlobCleanup>();

    public DbSet<GeneratedProfileRevision> GeneratedProfileRevisions => Set<GeneratedProfileRevision>();

    public DbSet<GeneratedProfileRevisionOperation> GeneratedProfileRevisionOperations =>
        Set<GeneratedProfileRevisionOperation>();

    public DbSet<CalibrationIdempotencyRecord> CalibrationIdempotencyRecords =>
        Set<CalibrationIdempotencyRecord>();

    public DbSet<CalibrationOrchestration> CalibrationOrchestrations =>
        Set<CalibrationOrchestration>();

    public DbSet<CalibrationChange> CalibrationChanges => Set<CalibrationChange>();

    public DbSet<CalibrationChangeFeedState> CalibrationChangeFeedStates =>
        Set<CalibrationChangeFeedState>();

    public DbSet<CalibrationSyncCursor> CalibrationSyncCursors => Set<CalibrationSyncCursor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Apply all IEntityTypeConfiguration classes from this assembly
        // This enables separation of entity configurations into individual files
        // in the Data/Configurations folder for better maintainability
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ConfigureCalibrationProviderSpecificIndexes(modelBuilder);

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

    private void ConfigureCalibrationProviderSpecificIndexes(ModelBuilder modelBuilder)
    {
        string filter = Database.ProviderName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "\"DeletedAtUtc\" IS NULL",
            "Microsoft.EntityFrameworkCore.SqlServer" => "[DeletedAtUtc] IS NULL",
            _ => "DeletedAtUtc IS NULL",
        };
        _ = modelBuilder.Entity<CalibrationDraft>()
            .HasIndex(draft => new
            {
                draft.ProjectId,
                draft.StepId,
                draft.DeviceLineageId,
            })
            .HasFilter(filter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureCalibrationHistoryIsImmutable();
        EnsureCalibrationPrintersTracked();
        UpdateCalibrationConfigurationRevisions();
        PopulateCaseInsensitiveShadowColumns();
        StampRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureCalibrationHistoryIsImmutable();
        await EnsureCalibrationPrintersTrackedAsync(cancellationToken);
        UpdateCalibrationConfigurationRevisions();
        PopulateCaseInsensitiveShadowColumns();
        StampRowVersions();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureCalibrationHistoryIsImmutable()
    {
        ChangeTracker.DetectChanges();
        EnsureImmutable<PrinterConfigurationSnapshot>();
        EnsureImmutable<CalibrationAttempt>();
        EnsureImmutable<CalibrationAttemptEvent>();
        EnsureImmutable<CalibrationObservation>();
        EnsureImmutable<GeneratedProfileRevision>();
        EnsureImmutable<GeneratedProfileRevisionOperation>();
        EnsureImmutable<CalibrationChange>();
    }

    private void EnsureImmutable<TEntity>()
        where TEntity : class
    {
        foreach (EntityEntry<TEntity> entry in ChangeTracker.Entries<TEntity>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{typeof(TEntity).Name} rows are immutable calibration history.");
            }
        }
    }

    private void EnsureCalibrationPrintersTracked()
    {
        Guid[] printerIds = GetUntrackedChangedToolheadPrinterIds();
        if (printerIds.Length > 0)
        {
            Printers.Where(printer => printerIds.Contains(printer.Id)).Load();
        }
    }

    private async Task EnsureCalibrationPrintersTrackedAsync(
        CancellationToken cancellationToken)
    {
        Guid[] printerIds = GetUntrackedChangedToolheadPrinterIds();
        if (printerIds.Length > 0)
        {
            await Printers
                .Where(printer => printerIds.Contains(printer.Id))
                .LoadAsync(cancellationToken);
        }
    }

    private Guid[] GetUntrackedChangedToolheadPrinterIds()
    {
        ChangeTracker.DetectChanges();
        HashSet<Guid> trackedPrinterIds = ChangeTracker
            .Entries<Printer>()
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        return ChangeTracker
            .Entries<Toolhead>()
            .Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                !trackedPrinterIds.Contains(entry.Entity.PrinterId))
            .Select(entry => entry.Entity.PrinterId)
            .Distinct()
            .ToArray();
    }

    private void UpdateCalibrationConfigurationRevisions()
    {
        ChangeTracker.DetectChanges();
        DateTime changedAtUtc = DateTime.UtcNow;
        Dictionary<Guid, EntityEntry<Printer>> trackedPrinters = ChangeTracker
            .Entries<Printer>()
            .ToDictionary(entry => entry.Entity.Id);

        foreach (EntityEntry<Printer> entry in trackedPrinters.Values)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.ConfigurationRevision = Math.Max(1, entry.Entity.ConfigurationRevision);
                entry.Entity.CalibrationConfigurationUpdatedAtUtc ??= changedAtUtc;
                continue;
            }

            if (entry.State == EntityState.Modified &&
                entry.Properties.Any(property =>
                    property.IsModified &&
                    IsCalibrationRelevantPrinterProperty(property.Metadata.Name)))
            {
                IncrementCalibrationRevision(entry, changedAtUtc);
            }
        }

        foreach (EntityEntry<Toolhead> toolheadEntry in ChangeTracker.Entries<Toolhead>().ToArray())
        {
            if (toolheadEntry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted) ||
                !trackedPrinters.TryGetValue(toolheadEntry.Entity.PrinterId, out EntityEntry<Printer>? printerEntry))
            {
                continue;
            }

            if (toolheadEntry.State != EntityState.Modified ||
                toolheadEntry.Properties.Any(property =>
                    property.IsModified &&
                    IsCalibrationRelevantToolheadProperty(property.Metadata.Name)))
            {
                IncrementCalibrationRevision(printerEntry, changedAtUtc);
            }
        }
    }

    private static bool IsCalibrationRelevantPrinterProperty(string propertyName) =>
        propertyName is
            nameof(Printer.Backend) or
            nameof(Printer.ModelId) or
            nameof(Printer.MaxBuildVolumeX) or
            nameof(Printer.MaxBuildVolumeY) or
            nameof(Printer.MaxBuildVolumeZ) or
            nameof(Printer.MaxBedTemp) or
            nameof(Printer.MaxPrintSpeed) or
            nameof(Printer.FirmwareFamily) or
            nameof(Printer.GcodeDialect) or
            nameof(Printer.FirmwareDetectionSource) or
            nameof(Printer.FirmwareVersion) or
            nameof(Printer.FirmwareDetectionVersion) or
            nameof(Printer.FirmwareDetectionConfidence) or
            nameof(Printer.FirmwareDetectedAtUtc) or
            nameof(Printer.FirmwareIdentityVerified) or
            nameof(Printer.BackendVersion) or
            nameof(Printer.BackendApiVersion) or
            nameof(Printer.BedOriginX) or
            nameof(Printer.BedOriginY) or
            nameof(Printer.PrintablePolygonJson) or
            nameof(Printer.ExcludedRegionsJson) or
            nameof(Printer.CalibrationMotionType) or
            nameof(Printer.MaxTravelSpeed) or
            nameof(Printer.MaxAcceleration) or
            nameof(Printer.MaxTravelAcceleration) or
            nameof(Printer.CalibrationHasHeatedBed) or
            nameof(Printer.CalibrationHasEnclosure) or
            nameof(Printer.HasHeatedChamber) or
            nameof(Printer.MaxChamberTemp) or
            nameof(Printer.ActiveToolheadIndex) or
            nameof(Printer.SupportsPressureAdvance) or
            nameof(Printer.SupportsFirmwareRetraction) or
            nameof(Printer.CalibrationHardwareVerifiedAtUtc) or
            nameof(Printer.CalibrationSlicerEngine) or
            nameof(Printer.CalibrationSlicerDistribution) or
            nameof(Printer.CalibrationSlicerVersion) or
            nameof(Printer.CalibrationProfileFormat) or
            nameof(Printer.CalibrationMachineProfileId) or
            nameof(Printer.CalibrationProcessProfileId) or
            nameof(Printer.CalibrationFilamentProfileId);

    private static bool IsCalibrationRelevantToolheadProperty(string propertyName) =>
        propertyName is
            nameof(Toolhead.Name) or
            nameof(Toolhead.Index) or
            nameof(Toolhead.IsPrimary) or
            nameof(Toolhead.ToolheadType) or
            nameof(Toolhead.SupportedMaterials) or
            nameof(Toolhead.OffsetX) or
            nameof(Toolhead.OffsetY) or
            nameof(Toolhead.OffsetZ) or
            nameof(Toolhead.NozzleDiameter) or
            nameof(Toolhead.NozzleType) or
            nameof(Toolhead.NozzleMaterial) or
            nameof(Toolhead.NozzleMaxTemperature) or
            nameof(Toolhead.NozzleIsHardened) or
            nameof(Toolhead.HotendMaxTemperature) or
            nameof(Toolhead.MaxVolumetricFlow) or
            nameof(Toolhead.DriveType) or
            nameof(Toolhead.IsDirectDrive) or
            nameof(Toolhead.ExtruderGearRatio);

    private static void IncrementCalibrationRevision(
        EntityEntry<Printer> entry,
        DateTime changedAtUtc)
    {
        if (entry.State is EntityState.Added or EntityState.Deleted)
        {
            return;
        }

        if (!entry.Property(p => p.ConfigurationRevision).IsModified)
        {
            entry.Entity.ConfigurationRevision = Math.Max(1, entry.Entity.ConfigurationRevision) + 1;
            entry.Property(p => p.ConfigurationRevision).IsModified = true;
        }

        entry.Entity.CalibrationConfigurationUpdatedAtUtc = changedAtUtc;
        entry.Property(p => p.CalibrationConfigurationUpdatedAtUtc).IsModified = true;
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
