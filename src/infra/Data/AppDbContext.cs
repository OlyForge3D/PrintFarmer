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

    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

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

    // NFC Devices (ESP32 + PN532 filament spool readers)
    public DbSet<NfcDevice> NfcDevices => Set<NfcDevice>();

    public DbSet<NfcScanEvent> NfcScanEvents => Set<NfcScanEvent>();

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Apply all IEntityTypeConfiguration classes from this assembly
        // This enables separation of entity configurations into individual files
        // in the Data/Configurations folder for better maintainability
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

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
        PopulateCaseInsensitiveShadowColumns();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PopulateCaseInsensitiveShadowColumns();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
}
