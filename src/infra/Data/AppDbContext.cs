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

    public DbSet<MutationCounter> MutationCounters => Set<MutationCounter>();

    // Print approvals (pending Upload+Print approvals)
    public DbSet<PrintApproval> PrintApprovals => Set<PrintApproval>();

    // File Health & Consistency Auditing
    public DbSet<FileHealthAudit> FileHealthAudits => Set<FileHealthAudit>();

    // Notifications & User Communication
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    // Native push device tokens (iOS APNs today; Android/FCM reserved) — see #708.
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

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

    // Ordered same-material fallback chains over existing Toolheads (issue #711, F6).
    public DbSet<FilamentFallbackGroup> FilamentFallbackGroups => Set<FilamentFallbackGroup>();

    public DbSet<FilamentFallbackGroupMember> FilamentFallbackGroupMembers => Set<FilamentFallbackGroupMember>();

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

    /// <summary>Durable checkpoints for slicer artifact to G-code library promotions.</summary>
    public DbSet<GcodePromotionCheckpoint> GcodePromotionCheckpoints =>
        Set<GcodePromotionCheckpoint>();

    // =========================================================================
    // Issue #900: Calibration queue dispatch durability
    // =========================================================================

    /// <summary>
    /// Durable scheduling outbox events written in the same transaction as
    /// queue state changes so events survive process crashes.
    /// </summary>
    public DbSet<QueueDispatchOutbox> QueueDispatchOutbox => Set<QueueDispatchOutbox>();

    /// <summary>
    /// Per-attempt record for each database-backed dispatch claim.
    /// One row per start-path invocation; persists even for unknown outcomes
    /// so reconciliation can identify orphaned Starting jobs.
    /// </summary>
    public DbSet<QueueDispatchAttempt> QueueDispatchAttempts => Set<QueueDispatchAttempt>();

    /// <summary>
    /// Single-row cross-process monotonic sequence counter for the outbox.
    /// Incremented atomically in the same transaction as each outbox event write.
    /// </summary>
    public DbSet<OutboxSequenceState> OutboxSequenceStates => Set<OutboxSequenceState>();

    /// <summary>
    /// Durable actor/resource/operation/outcome audit rows for safety-sensitive queue and
    /// dispatch operations. Written in the same transaction as the operation they record.
    /// </summary>
    public DbSet<QueueOperationAudit> QueueOperationAudits => Set<QueueOperationAudit>();

    /// <summary>Durable exact-job bed-clear command idempotency records.</summary>
    public DbSet<BedClearCommandRecord> BedClearCommandRecords => Set<BedClearCommandRecord>();

    /// <summary>Provider-native per-printer queue position counters.</summary>
    public DbSet<QueuePositionState> QueuePositionStates => Set<QueuePositionState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Apply all IEntityTypeConfiguration classes from this assembly
        // This enables separation of entity configurations into individual files
        // in the Data/Configurations folder for better maintainability
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ConfigureCalibrationProviderSpecificIndexes(modelBuilder);
        ConfigureQueueDispatchIndexes(modelBuilder);

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

        // Finding H7 (issue #711): one completion log per resolved maintenance alert. A UNIQUE
        // filtered index on ResolvedAlertId restricted to non-null values makes a duplicate
        // completion insert impossible even under a concurrent retry race, while leaving legacy
        // logs (ResolvedAlertId IS NULL) unconstrained. The filter SQL is provider-specific, so it
        // must be declared here rather than in MaintenanceLogConfiguration. Providers without
        // filtered-index support fall back to a plain non-unique index (best-effort).
        Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder<MaintenanceLog> resolvedAlertIndex =
            modelBuilder.Entity<MaintenanceLog>()
                .HasIndex(l => l.ResolvedAlertId)
                .HasDatabaseName("IX_MaintenanceLogs_ResolvedAlertId");

        switch (Database.ProviderName)
        {
            case "Npgsql.EntityFrameworkCore.PostgreSQL":
            case "Microsoft.EntityFrameworkCore.Sqlite":
                _ = resolvedAlertIndex.IsUnique().HasFilter("\"ResolvedAlertId\" IS NOT NULL");
                break;
            case "Microsoft.EntityFrameworkCore.SqlServer":
                _ = resolvedAlertIndex.IsUnique().HasFilter("[ResolvedAlertId] IS NOT NULL");
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

        // Fix (#715): SQL Server's default catalog collation
        // (SQL_Latin1_General_CP1_CI_AS) is a LINGUISTIC collation whose equivalence
        // classes diverge from BOTH ordinal .NET comparison AND Unicode NFKC folding.
        // It is case-INSENSITIVE ("ABC" == "abc"), width-INSENSITIVE (fullwidth ＡＢＣ ==
        // ABC), Kana-type-INSENSITIVE (Hiragana か U+304B == Katakana カ U+30AB), and
        // folds assorted Latin phonetic letters onto their ASCII base (small-capital I
        // U+026A == "i"; dotless ı U+0131 == "i"). PostgreSQL and SQLite compare these
        // columns byte-exact (deterministic collation), so on SQL Server ALONE a value
        // whose identity is DISTINCT to the application (ordinal, post-NFKC) can collapse
        // onto the SAME physical row — double-applying a stock delta, or false-deduping a
        // genuinely distinct operation, under a single Idempotency-Key.
        //
        // NFKC (Apone r5) plus the ordinal reserved-prefix guard align the app with SOME
        // of those classes, but not all: NFKC does not fold Kana か/カ (Hicks r5 blocker 1)
        // nor small-capital I U+026A (Hicks r5 blocker 2). Chasing each linguistic
        // equivalence in application code is a losing game. Instead we converge at the
        // storage layer: every client-controlled identity/idempotency column that backs a
        // unique index is forced to a binary, culture-invariant, case-sensitive collation.
        // Byte-exact SQL comparison then matches the app's ordinal comparison exactly,
        // closing ALL SQL-vs-app mismatch classes at once. Kane r1 established this for
        // IdempotencyRecords; r6 extends it to the printed-part identity columns. NFKC is
        // retained as advisory defense-in-depth for any non-DB code path.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
        {
            const string caseSensitiveCollation = "Latin1_General_100_BIN2";

            // Native-push installation identifiers are opaque client identities. PostgreSQL
            // and SQLite compare their ASCII wire form case-sensitively; pin SQL Server to
            // the same bytewise semantic before applying the composite unique index.
            _ = modelBuilder.Entity<DeviceToken>(entity =>
                entity.Property(token => token.InstallationId).UseCollation(caseSensitiveCollation));

            _ = modelBuilder.Entity<IdempotencyRecord>(entity =>
            {
                _ = entity.Property(r => r.UserId).UseCollation(caseSensitiveCollation);
                _ = entity.Property(r => r.RouteKey).UseCollation(caseSensitiveCollation);
                _ = entity.Property(r => r.IdempotencyKey).UseCollation(caseSensitiveCollation);
            });

            // Printed-part SKU catalog: Sku is the client-owned identity behind the unique
            // index IX_PartInventories_Sku (Hicks r5 blocker 1 — Hiragana vs Katakana SKUs
            // resolving to one physical row under Kana-insensitive collation).
            _ = modelBuilder.Entity<PartInventory>(entity =>
                entity.Property(p => p.Sku).UseCollation(caseSensitiveCollation));

            // Bin barcodes share the SKU normalization pathway (PartInventoryIdentity
            // .NormalizeBinCode) and back the unique index IX_Bins_Code; identical class.
            _ = modelBuilder.Entity<Bin>(entity =>
                entity.Property(b => b.Code).UseCollation(caseSensitiveCollation));

            // Natural idempotency backstop: (PartInventoryId, OperationKey) unique index.
            // OperationKey is persisted client-verbatim (trimmed only — never NFKC-folded),
            // so the store MUST compare it byte-exact (Hicks r5 blocker 2 — small-capital I
            // U+026A false-deduping against a server-synthesized "idem:" key).
            _ = modelBuilder.Entity<PartInventoryAdjustment>(entity =>
                entity.Property(a => a.OperationKey).UseCollation(caseSensitiveCollation));

            // Harvest idempotency key: the client-verbatim (trimmed) value behind the
            // unique index IX_PrintJobs_HarvestOperationKey. This is the harvest-path twin
            // of PartInventoryAdjustment.OperationKey and is exposed to the identical
            // collation-mismatch class, so it converges to BIN2 alongside it.
            _ = modelBuilder.Entity<PrintJob>(entity =>
                entity.Property(pj => pj.HarvestOperationKey).UseCollation(caseSensitiveCollation));

            // Canonical spool identities preserve path case. Keep SQL Server comparisons
            // aligned with the application's ordinal source-identity semantics.
            _ = modelBuilder.Entity<PrintJobToolheadUsage>(entity =>
                entity.Property(usage => usage.SpoolSourceIdentity)
                    .UseCollation(caseSensitiveCollation));
        }

        // For SQLite and PostgreSQL, row-version columns are application-managed (the DB does not
        // auto-generate them). Override the IsRowVersion() store-generated setting so
        // EF Core does not try to round-trip the DB value after each save, allowing
        // StampRowVersions() to write a non-null GUID token on every Add/Modify.
        // SQL Server uses a native ROWVERSION column that the DB generates and returns.
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.SqlServer")
        {
            _ = modelBuilder.Entity<Printer>()
                .Property(printer => printer.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<PrintJob>()
                .Property(j => j.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<PrinterDispatchState>()
                .Property(s => s.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();

            // Outbox event RowVersion: application-managed on SQLite/PostgreSQL.
            _ = modelBuilder.Entity<QueueDispatchOutbox>()
                .Property(o => o.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();

            // OutboxSequenceState RowVersion: application-managed on SQLite/PostgreSQL.
            _ = modelBuilder.Entity<OutboxSequenceState>()
                .Property(s => s.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<QueueDispatchAttempt>()
                .Property(a => a.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<DispatchSettings>()
                .Property(s => s.RowVersion)
                .HasMaxLength(16)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        }
        else
        {
            // SQL Server: RowVersion is a native database-generated ROWVERSION column.
            _ = modelBuilder.Entity<QueueDispatchOutbox>()
                .Property(o => o.RowVersion)
                .IsRowVersion();
            _ = modelBuilder.Entity<OutboxSequenceState>()
                .Property(s => s.RowVersion)
                .IsRowVersion();
            _ = modelBuilder.Entity<QueueDispatchAttempt>()
                .Property(a => a.RowVersion)
                .IsRowVersion();
            _ = modelBuilder.Entity<DispatchSettings>()
                .Property(s => s.RowVersion)
                .IsRowVersion();
        }

        // Seed the single OutboxSequenceState row (Id = 1, NextSequence = 0).
        // This row must exist before any outbox event can be written.
        _ = modelBuilder.Entity<OutboxSequenceState>()
            .HasData(new OutboxSequenceState { Id = 1, NextSequence = 0 });

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

    /// <summary>
    /// Configures provider-specific filtered unique indexes for calibration queue dispatch.
    /// The idempotency index only covers active (non-terminal) calibration jobs so that a
    /// terminal job with the same key does not block a new attempt.
    /// PrintJobStatus values: Queued=0, Assigned=1, Starting=2, Printing=3, Paused=4,
    /// Completed=5, Failed=6, Cancelled=7 — we exclude 5/6/7 (terminal).
    /// </summary>
    private void ConfigureQueueDispatchIndexes(ModelBuilder modelBuilder)
    {
        // Filtered unique index on (IdempotencyScope, IdempotencyKey) for active calibration jobs.
        string idempotencyFilter = Database.ProviderName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" =>
                "\"IdempotencyScope\" IS NOT NULL AND \"IdempotencyKey\" IS NOT NULL AND \"JobKind\" = 1",
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                "[IdempotencyScope] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [JobKind] = 1",
            _ =>
                "IdempotencyScope IS NOT NULL AND IdempotencyKey IS NOT NULL AND JobKind = 1",
        };

        _ = modelBuilder.Entity<PrintJob>()
            .HasIndex(j => new { j.IdempotencyScope, j.IdempotencyKey })
            .HasFilter(idempotencyFilter)
            .IsUnique()
            .HasDatabaseName("IX_PrintJobs_Idempotency_Calibration");

        _ = modelBuilder.Entity<JobSchedule>()
            .HasIndex(schedule => schedule.RootPrintJobId)
            .HasDatabaseName("IX_JobSchedules_RootPrintJobId");

        _ = modelBuilder.Entity<JobExecution>()
            .HasIndex(execution => execution.OccurrencePrintJobId)
            .HasDatabaseName("IX_JobExecutions_OccurrencePrintJobId");

        // Dispatch-attempt indexes.
        _ = modelBuilder.Entity<QueueDispatchAttempt>()
            .HasIndex(a => new { a.PrintJobId, a.AttemptNumber })
            .HasDatabaseName("IX_QueueDispatchAttempts_Job_Attempt");

        _ = modelBuilder.Entity<QueueDispatchAttempt>()
            .HasIndex(a => new { a.PrinterId, a.Outcome })
            .HasDatabaseName("IX_QueueDispatchAttempts_Printer_Outcome");

        // Pending outbox events are polled frequently.
        _ = modelBuilder.Entity<QueueDispatchOutbox>()
            .HasIndex(o => new { o.Status, o.RetryAfterUtc })
            .HasDatabaseName("IX_QueueDispatchOutbox_Status_RetryAfterUtc");

        // Unique monotonic sequence: enforces per-process allocator uniqueness at the DB level.
        // Catches any collision (e.g., multi-process deployment) as a constraint violation.
        _ = modelBuilder.Entity<QueueDispatchOutbox>()
            .HasIndex(o => o.Sequence)
            .IsUnique()
            .HasDatabaseName("UX_QueueDispatchOutbox_Sequence");

        // Audit lookup patterns: by resource, by printer, and chronologically.
        _ = modelBuilder.Entity<QueueOperationAudit>()
            .HasIndex(a => new { a.ResourceType, a.ResourceId })
            .HasDatabaseName("IX_QueueOperationAudits_Resource");

        _ = modelBuilder.Entity<QueueOperationAudit>()
            .HasIndex(a => new { a.PrinterId, a.OccurredAtUtc })
            .HasDatabaseName("IX_QueueOperationAudits_Printer_OccurredAt");

        _ = modelBuilder.Entity<QueueOperationAudit>()
            .HasIndex(a => a.OccurredAtUtc)
            .HasDatabaseName("IX_QueueOperationAudits_OccurredAt");

        _ = modelBuilder.Entity<BedClearCommandRecord>()
            .HasIndex(record => new { record.PrinterId, record.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_BedClearCommandRecords_Printer_Key");

        _ = modelBuilder.Entity<BedClearCommandRecord>()
            .HasIndex(record => new { record.Status, record.ExpiresAtUtc })
            .HasDatabaseName("IX_BedClearCommandRecords_Status_Expiry");

        _ = modelBuilder.Entity<QueuePositionState>()
            .HasKey(state => state.ScopeId);

        string queuePositionFilter = Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer"
            ? "[AssignedPrinterId] IS NOT NULL AND [Status] IN (0, 1)"
            : "\"AssignedPrinterId\" IS NOT NULL AND \"Status\" IN (0, 1)";
        _ = modelBuilder.Entity<PrintJob>()
            .HasIndex(job => new { job.AssignedPrinterId, job.QueuePosition })
            .IsUnique()
            .HasFilter(queuePositionFilter)
            .HasDatabaseName("UX_PrintJobs_Printer_QueuePosition");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsurePartInventoryLedgerIsAppendOnly();
        EnsureInventoryIdentitiesAreImmutable();
        EnsureCalibrationHistoryIsImmutable();
        EnsureCalibrationJobFieldsAreImmutable();
        EnsureCalibrationPrintersTracked();
        UpdateCalibrationConfigurationRevisions();
        PopulateCaseInsensitiveShadowColumns();
        NormalizeActiveExternalPrintKeys();
        AdvanceLogicalQueueRevisions();
        StampRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsurePartInventoryLedgerIsAppendOnly();
        EnsureInventoryIdentitiesAreImmutable();
        EnsureCalibrationHistoryIsImmutable();
        EnsureCalibrationJobFieldsAreImmutable();
        await EnsureCalibrationPrintersTrackedAsync(cancellationToken);
        UpdateCalibrationConfigurationRevisions();
        PopulateCaseInsensitiveShadowColumns();
        NormalizeActiveExternalPrintKeys();
        AdvanceLogicalQueueRevisions();
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

    /// <summary>
    /// Prevents mutation of immutable calibration/provenance/idempotency/compatibility
    /// fields on <see cref="PrintJob"/> after creation. These fields are stamped once at
    /// job creation time and must never change; changing them would invalidate the
    /// canonical idempotency hash and break replay semantics.
    ///
    /// The guard keys off the ORIGINAL (database) <see cref="PrintJob.JobKind"/>, never the
    /// current value: flipping a calibration job to <c>Standard</c> in the same save must not
    /// disarm the guard. <see cref="PrintJob.JobKind"/> itself is rejected for any job whose
    /// original kind was set, so a Standard job can never be laundered into a calibration job
    /// (or the reverse) after creation.
    /// </summary>
    private void EnsureCalibrationJobFieldsAreImmutable()
    {
        foreach (EntityEntry<PrintJob> entry in ChangeTracker.Entries<PrintJob>())
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            // JobKind itself is immutable for every persisted job whose kind is already set.
            // Rejecting the mutation outright closes the "flip to Standard in the same save"
            // bypass. Legacy rows with a NULL kind may still be backfilled exactly once.
            object? originalKind = entry.OriginalValues[nameof(PrintJob.JobKind)];
            if (originalKind is not null)
            {
                CheckImmutableField(entry, nameof(PrintJob.JobKind));
            }

            // Only jobs that were ORIGINALLY calibration jobs carry the provenance constraint.
            if (originalKind is not JobKind.FilamentCalibration)
            {
                continue;
            }

            // The following fields are immutable once a calibration PrintJob is created.
            CheckImmutableField(entry, nameof(PrintJob.IdempotencyScope));
            CheckImmutableField(entry, nameof(PrintJob.IdempotencyKey));
            CheckImmutableField(entry, nameof(PrintJob.IdempotencyRequestSha256));
            CheckImmutableField(entry, nameof(PrintJob.CalibrationProjectId));
            CheckImmutableField(entry, nameof(PrintJob.CalibrationAttemptId));
            CheckImmutableField(entry, nameof(PrintJob.CalibrationOrchestrationId));
            CheckImmutableField(entry, nameof(PrintJob.CalibrationConfigSnapshotId));
            CheckImmutableField(entry, nameof(PrintJob.SourceArtifactId));
            CheckImmutableField(entry, nameof(PrintJob.SliceJobId));
            CheckImmutableField(entry, nameof(PrintJob.CreatorSubject));
            CheckImmutableField(entry, nameof(PrintJob.GcodeFileId));
            CheckImmutableField(entry, nameof(PrintJob.AssignedPrinterId));
            CheckImmutableField(entry, nameof(PrintJob.Priority));
            CheckImmutableField(entry, nameof(PrintJob.Copies));
            CheckImmutableField(entry, nameof(PrintJob.RequiredNozzleDiameter));
            CheckImmutableField(entry, nameof(PrintJob.RequiredMaterialType));
            CheckImmutableField(entry, nameof(PrintJob.RequiredCapabilities));
            CheckImmutableField(entry, nameof(PrintJob.RequiredFirmwareFamily));
            CheckImmutableField(entry, nameof(PrintJob.RequiredGcodeDialect));
            CheckImmutableField(entry, nameof(PrintJob.RequiredSlicerEngine));
            CheckImmutableField(entry, nameof(PrintJob.RequiredSlicerDistribution));
            CheckImmutableField(entry, nameof(PrintJob.RequiredSlicerVersion));
            CheckImmutableField(entry, nameof(PrintJob.RequiredSlicerContainerDigest));
            CheckImmutableField(entry, nameof(PrintJob.GcodeContentSha256));
            CheckImmutableField(entry, nameof(PrintJob.FilamentProfileSha256));
            CheckImmutableField(entry, nameof(PrintJob.MachineProfileSha256));
            CheckImmutableField(entry, nameof(PrintJob.ProcessProfileSha256));
            CheckImmutableField(entry, nameof(PrintJob.SpecificationSha256));
            CheckImmutableField(entry, nameof(PrintJob.PrinterConfigSnapshotSha256));
            CheckImmutableField(entry, nameof(PrintJob.PinnedPrinterConfigRevision));
            CheckImmutableField(entry, nameof(PrintJob.PinnedGcodeFileSizeBytes));
            CheckImmutableField(entry, nameof(PrintJob.PinnedPrinterModelId));
            CheckImmutableField(entry, nameof(PrintJob.PinnedToolheadId));
            CheckImmutableField(entry, nameof(PrintJob.PinnedToolheadIndex));
            CheckImmutableField(entry, nameof(PrintJob.PinnedSpoolId));
            CheckImmutableField(entry, nameof(PrintJob.PinnedFilamentSku));
            CheckImmutableField(entry, nameof(PrintJob.PinnedFilamentLotNumber));
            CheckImmutableField(entry, nameof(PrintJob.FilamentSnapshotSha256));
            CheckImmutableField(entry, nameof(PrintJob.SourceModelSha256));
            CheckImmutableField(entry, nameof(PrintJob.CalibrationManifestSha256));
            CheckImmutableField(entry, nameof(PrintJob.PinnedObjectDimensionX));
            CheckImmutableField(entry, nameof(PrintJob.PinnedObjectDimensionY));
            CheckImmutableField(entry, nameof(PrintJob.PinnedObjectDimensionZ));
            CheckImmutableField(entry, nameof(PrintJob.EstimatedFilamentUsage));
            CheckImmutableField(entry, nameof(PrintJob.FilamentName));
            CheckImmutableField(entry, nameof(PrintJob.FilamentVendor));
            CheckImmutableField(entry, nameof(PrintJob.FilamentColor));
        }
    }

    private static void CheckImmutableField(EntityEntry<PrintJob> entry, string propertyName)
    {
        PropertyEntry? prop = entry.Properties.FirstOrDefault(p => p.Metadata.Name == propertyName);
        if (prop is { IsModified: true })
        {
            throw new InvalidOperationException(
                $"PrintJob.{propertyName} is an immutable calibration provenance field and cannot be modified after creation. " +
                "Changed immutable input requires a new job and idempotency key.");
        }
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

        // Provider-correct non-null application-managed concurrency tokens.
        // SQL Server uses a native ROWVERSION column (stamped automatically by the database);
        // SQLite and PostgreSQL do NOT generate non-null tokens from [Timestamp] alone, so we
        // stamp a fresh GUID-derived byte array on every write so the concurrency check never
        // compares NULL == NULL (which would allow multiple concurrent winners).
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.SqlServer")
        {
            foreach (EntityEntry<Printer> entry in ChangeTracker.Entries<Printer>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }

            foreach (EntityEntry<PrintJob> entry in ChangeTracker.Entries<PrintJob>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }

            foreach (EntityEntry<PrinterDispatchState> entry in ChangeTracker.Entries<PrinterDispatchState>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }

            // Outbox events: stamp on every write for atomic lease detection.
            foreach (EntityEntry<QueueDispatchOutbox> entry in ChangeTracker.Entries<QueueDispatchOutbox>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }

            // Sequence counter: stamp on every write so the concurrency check fires.
            foreach (EntityEntry<OutboxSequenceState> entry in ChangeTracker.Entries<OutboxSequenceState>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }

            foreach (EntityEntry<QueueDispatchAttempt> entry in ChangeTracker.Entries<QueueDispatchAttempt>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }

            foreach (EntityEntry<DispatchSettings> entry in ChangeTracker.Entries<DispatchSettings>())
            {
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    entry.Entity.RowVersion = newVersion;
                }
            }
        }
    }

    private void NormalizeActiveExternalPrintKeys()
    {
        foreach (EntityEntry<PrintJob> entry in ChangeTracker.Entries<PrintJob>()
                     .Where(candidate => candidate.State is EntityState.Added or EntityState.Modified))
        {
            bool activeExternal =
                entry.Entity.IsExternalPrint &&
                entry.Entity.AssignedPrinterId.HasValue &&
                entry.Entity.Status is PrintJobStatus.Starting or
                    PrintJobStatus.Printing or
                    PrintJobStatus.Paused;
            entry.Entity.ActiveExternalPrinterId = activeExternal
                ? entry.Entity.AssignedPrinterId
                : null;
        }
    }

    private void AdvanceLogicalQueueRevisions()
    {
        foreach (EntityEntry<PrintJob> entry in ChangeTracker.Entries<PrintJob>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Revision = 1;
            }
            else if (entry.State == EntityState.Modified)
            {
                long originalRevision = entry.Property(job => job.Revision).OriginalValue;
                entry.Entity.Revision = Math.Max(1, originalRevision) + 1;
                entry.Property(job => job.Revision).IsModified = true;
            }
        }

        foreach (EntityEntry<PrinterDispatchState> entry in
                 ChangeTracker.Entries<PrinterDispatchState>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Revision = 1;
            }
            else if (entry.State == EntityState.Modified)
            {
                long originalRevision = entry.Property(state => state.Revision).OriginalValue;
                entry.Entity.Revision = Math.Max(1, originalRevision) + 1;
                entry.Property(state => state.Revision).IsModified = true;
            }
        }

        foreach (EntityEntry<DispatchSettings> entry in ChangeTracker.Entries<DispatchSettings>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Revision = 1;
            }
            else if (entry.State == EntityState.Modified)
            {
                long originalRevision = entry.Property(settings => settings.Revision).OriginalValue;
                entry.Entity.Revision = Math.Max(1, originalRevision) + 1;
                entry.Property(settings => settings.Revision).IsModified = true;
            }
        }

        foreach (EntityEntry<QueueDispatchOutbox> eventEntry in
                 ChangeTracker.Entries<QueueDispatchOutbox>()
                     .Where(entry => entry.State == EntityState.Added))
        {
            PrintJob? job = ChangeTracker.Entries<PrintJob>()
                .Select(entry => entry.Entity)
                .FirstOrDefault(candidate => candidate.Id == eventEntry.Entity.AggregateId);
            if (job is not null)
            {
                eventEntry.Entity.JobRevision = job.Revision;
            }

            if (eventEntry.Entity.PrinterId.HasValue)
            {
                PrinterDispatchState? state =
                    ChangeTracker.Entries<PrinterDispatchState>()
                        .Select(entry => entry.Entity)
                        .FirstOrDefault(candidate =>
                            candidate.PrinterId == eventEntry.Entity.PrinterId.Value);
                if (state is not null)
                {
                    eventEntry.Entity.DispatchStateRevision = state.Revision;
                }
            }
        }
    }
}
