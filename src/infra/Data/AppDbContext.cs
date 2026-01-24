using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSettingsEntity> AppSettingsEntities => Set<AppSettingsEntity>();

    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();

    public DbSet<Printer> Printers => Set<Printer>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Spool> Spools => Set<Spool>();

    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();

    public DbSet<PrinterModel> PrinterModels => Set<PrinterModel>();

    public DbSet<PrinterModelAlias> PrinterModelAliases => Set<PrinterModelAlias>();

    public DbSet<PrinterModelToolhead> PrinterModelToolheads => Set<PrinterModelToolhead>();

    public DbSet<FilamentType> FilamentTypes => Set<FilamentType>();

    public DbSet<SpoolmanConfig> SpoolmanConfigs => Set<SpoolmanConfig>();

    // G-code Library & Job Queue
    public DbSet<GcodeFile> GcodeFiles => Set<GcodeFile>();

    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();

    public DbSet<JobStateHistory> JobStateHistories => Set<JobStateHistory>();

    public DbSet<JobSchedule> JobSchedules => Set<JobSchedule>();

    public DbSet<JobExecution> JobExecutions => Set<JobExecution>();

    public DbSet<PrintJobStatistics> PrintJobStatistics => Set<PrintJobStatistics>();

    public DbSet<RetryPolicy> RetryPolicies => Set<RetryPolicy>();

    public DbSet<JobRetry> JobRetries => Set<JobRetry>();

    public DbSet<Toolhead> Toolheads => Set<Toolhead>();

    public DbSet<GcodeHarvestOperation> GcodeHarvestOperations => Set<GcodeHarvestOperation>();

    public DbSet<HarvestDiscoveredFile> HarvestDiscoveredFiles => Set<HarvestDiscoveredFile>();

    public DbSet<HarvestFileGcodeFileMapping> HarvestFileGcodeFileMappings => Set<HarvestFileGcodeFileMapping>();

    public DbSet<GcodeHarvestQueueItem> GcodeHarvestQueueItems => Set<GcodeHarvestQueueItem>();

    // 3D Model Management & Slicer Integration
    public DbSet<Model3D> Models3D => Set<Model3D>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<FolderNode> Folders => Set<FolderNode>();

    public DbSet<ProcessProfile> ProcessProfiles => Set<ProcessProfile>();

    public DbSet<MachineModelProfile> MachineModelProfiles => Set<MachineModelProfile>();

    public DbSet<MachineProfile> MachineProfiles => Set<MachineProfile>();

    public DbSet<FilamentProfile> FilamentProfiles => Set<FilamentProfile>();

    public DbSet<SlicerSettings> SlicerSettings => Set<SlicerSettings>();

    public DbSet<SlicerService> SlicerServices => Set<SlicerService>();

    public DbSet<SliceJob> SliceJobs => Set<SliceJob>();

    public DbSet<Worker> Workers => Set<Worker>();

    public DbSet<UserTask> UserTasks => Set<UserTask>();

    // Slicing artifacts (G-code outputs, thumbnails, logs, previews)
    public DbSet<Artifact> Artifacts => Set<Artifact>();

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

    public DbSet<MaintenanceSchedule> MaintenanceSchedules => Set<MaintenanceSchedule>();

    public DbSet<MaintenanceLog> MaintenanceLogs => Set<MaintenanceLog>();

    public DbSet<MaintenanceAlert> MaintenanceAlerts => Set<MaintenanceAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // AppSettingsEntity Entity Configuration
        _ = modelBuilder.Entity<AppSettingsEntity>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.Key).IsRequired().HasMaxLength(128);
            _ = b.Property(a => a.SettingsJson).IsRequired().HasColumnType("TEXT");
            _ = b.Property(a => a.UpdatedAt).IsRequired();
            _ = b.HasIndex(a => a.Key).IsUnique();
        });

        // SystemLog Entity Configuration
        _ = modelBuilder.Entity<SystemLog>(b =>
        {
            _ = b.HasKey(l => l.Id);
            _ = b.Property(l => l.CorrelationId).HasMaxLength(64);
            _ = b.Property(l => l.Exception).HasColumnType("TEXT");
            _ = b.Property(l => l.Level).IsRequired().HasMaxLength(32);
            _ = b.Property(l => l.Message).IsRequired().HasMaxLength(1024);
            _ = b.Property(l => l.Metadata).HasColumnType("TEXT");
            _ = b.Property(l => l.Source).HasMaxLength(128);
            _ = b.Property(l => l.Timestamp).IsRequired();
            _ = b.HasIndex(l => l.Timestamp);
            _ = b.HasIndex(l => l.Level);
        });
        ArgumentNullException.ThrowIfNull(modelBuilder);
        _ = modelBuilder.Entity<Printer>(b =>
        {
            _ = b.HasKey(p => p.Id);
            _ = b.Property(p => p.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(p => p.ServerUrl).IsRequired().HasMaxLength(256);
            _ = b.Property(p => p.OriginalServerUrl).HasMaxLength(256);
            _ = b.Property(p => p.IpAddress).HasMaxLength(64);
            _ = b.Property(p => p.Backend).HasDefaultValue(0);
            _ = b.Property(p => p.ApiKey);

            // Prevent duplicate printers by IP address (unique constraint)
            // SQLite allows multiple NULLs in unique index by default
            // SQL Server needs a filtered index to allow NULLs
            IndexBuilder<Printer> ipIndex = b.HasIndex(p => p.IpAddress).IsUnique();
            if (Database.ProviderName != null && Database.ProviderName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                _ = ipIndex.HasFilter("[IpAddress] IS NOT NULL");
            }

            _ = b.HasOne(p => p.Manufacturer)
             .WithMany()
             .HasForeignKey(p => p.ManufacturerId)
             .OnDelete(DeleteBehavior.Restrict); // Changed from SetNull - ManufacturerId is not nullable
            _ = b.HasOne(p => p.Model)
             .WithMany()
             .HasForeignKey(p => p.ModelId)
             .OnDelete(DeleteBehavior.Restrict); // Changed from SetNull - ModelId is not nullable
            _ = b.HasOne(p => p.Location)
             .WithMany(l => l.Printers)
             .HasForeignKey(p => p.LocationId)
             .OnDelete(DeleteBehavior.SetNull); // Allow setting location to null
            _ = b.Property(p => p.DateAcquired);

            // Toolheads collection - one printer can have multiple hotends
            _ = b.HasMany(p => p.Toolheads)
             .WithOne(t => t.Printer)
             .HasForeignKey(t => t.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // TODO: ApiKey entity mapping - entity not yet implemented
        // _ = modelBuilder.Entity<Farm.Infrastructure.Domain.ApiKey>(b =>
        // {
        //     _ = b.HasKey(a => a.Id);
        //     _ = b.Property(a => a.UserId);
        //     _ = b.Property(a => a.Name).HasMaxLength(128);
        //     _ = b.Property(a => a.KeyHash).IsRequired().HasMaxLength(128);
        //     _ = b.Property(a => a.IsActive).HasDefaultValue(true);
        //     _ = b.HasIndex(a => a.KeyHash).IsUnique();
        //     _ = b.Property(a => a.CreatedAt).IsRequired();
        // });

        // Toolhead Entity Configuration
        _ = modelBuilder.Entity<Toolhead>(b =>
        {
            _ = b.HasKey(t => t.Id);
            _ = b.Property(t => t.Name).HasMaxLength(128);
            _ = b.Property(t => t.Index).IsRequired();
            _ = b.Property(t => t.IsPrimary).HasDefaultValue(false);
            _ = b.Property(t => t.UpdatedAt).IsRequired();

            // JSON array properties
            _ = b.Property(t => t.SupportedMaterials)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));

            // Foreign Key to Printer
            _ = b.HasOne(t => t.Printer)
             .WithMany(p => p.Toolheads)
             .HasForeignKey(t => t.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);

            // Foreign Keys to Component Models (optional relationships)
            _ = b.HasOne(t => t.HotendModel)
             .WithMany()
             .HasForeignKey(t => t.HotendModelId)
             .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(t => t.ExtruderModel)
             .WithMany()
             .HasForeignKey(t => t.ExtruderModelId)
             .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(t => t.ToolheadModelDef)
             .WithMany()
             .HasForeignKey(t => t.ToolheadModelDefId)
             .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(t => t.NozzleModel)
             .WithMany()
             .HasForeignKey(t => t.NozzleModelId)
             .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            _ = b.HasIndex(t => t.PrinterId);
            _ = b.HasIndex(t => t.Index);
        });

        // PrinterModelToolhead Entity Configuration (template toolheads for printer models)
        _ = modelBuilder.Entity<PrinterModelToolhead>(b =>
        {
            _ = b.HasKey(t => t.Id);
            _ = b.Property(t => t.Name).HasMaxLength(128);
            _ = b.Property(t => t.Index).IsRequired();
            _ = b.Property(t => t.IsPrimary).HasDefaultValue(false);

            // JSON array properties
            _ = b.Property(t => t.SupportedMaterials)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));

            // Foreign Key to PrinterModel
            _ = b.HasOne(t => t.PrinterModel)
             .WithMany(p => p.Toolheads)
             .HasForeignKey(t => t.PrinterModelId)
             .OnDelete(DeleteBehavior.Cascade);

            // Foreign Keys to Component Models (optional relationships)
            _ = b.HasOne(t => t.HotendModel)
             .WithMany()
             .HasForeignKey(t => t.HotendModelId)
             .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(t => t.ExtruderModel)
             .WithMany()
             .HasForeignKey(t => t.ExtruderModelId)
             .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(t => t.ToolheadModelDef)
             .WithMany()
             .HasForeignKey(t => t.ToolheadModelDefId)
             .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(t => t.NozzleModel)
             .WithMany()
             .HasForeignKey(t => t.NozzleModelId)
             .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            _ = b.HasIndex(t => t.PrinterModelId);
            _ = b.HasIndex(t => t.Index);
        });

        // Component Model Definitions (extensible manufacturer-backed components)
        ConfigureHotendModelDefinition(modelBuilder);
        ConfigureExtruderModelDefinition(modelBuilder);
        ConfigureToolheadModelDefinition(modelBuilder);
        ConfigureNozzleModelDefinition(modelBuilder);

        // Maintenance Module Configuration
        ConfigurePrinterStatistics(modelBuilder);
        ConfigureMaintenanceSchedule(modelBuilder);
        ConfigureMaintenanceLog(modelBuilder);
        ConfigureMaintenanceAlert(modelBuilder);

        // Location Entity Configuration
        _ = modelBuilder.Entity<Location>(b =>
        {
            _ = b.HasKey(l => l.Id);
            _ = b.Property(l => l.Name).IsRequired().HasMaxLength(256);
            _ = b.Property(l => l.Description).HasMaxLength(1024);
            _ = b.Property(l => l.PrinterCount).HasDefaultValue(0);
            _ = b.Property(l => l.CreatedAt).IsRequired();
            _ = b.Property(l => l.ModifiedAt).IsRequired();
            _ = b.Property(l => l.IsActive).HasDefaultValue(true);

            // One location can have many printers
            _ = b.HasMany(l => l.Printers)
             .WithOne(p => p.Location)
             .HasForeignKey(p => p.LocationId)
             .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            _ = b.HasIndex(l => l.Name).IsUnique();
            _ = b.HasIndex(l => l.IsActive);
            _ = b.HasIndex(l => l.CreatedAt);
        });

        _ = modelBuilder.Entity<Manufacturer>(b =>
        {
            _ = b.HasKey(m => m.Id);
            bool isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            PropertyBuilder<string> nameProp = b.Property(m => m.Name).IsRequired().HasMaxLength(128);
            if (isSqlite)
            {
                _ = nameProp.UseCollation("NOCASE");
            }

            // Persisted shadow column for cross-provider case-insensitive uniqueness.
            // We populate this in SaveChanges overrides (lower-invariant) to avoid provider-specific computed syntax.
            _ = b.Property<string>("NameLowered")
                .HasColumnName("NameLowered")
                .HasMaxLength(128)
                .IsRequired();
            _ = b.HasIndex("NameLowered").IsUnique();
        });

        _ = modelBuilder.Entity<PrinterModel>(b =>
        {
            _ = b.HasKey(m => m.Id);
            bool isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            PropertyBuilder<string> nameProp = b.Property(m => m.Name).IsRequired().HasMaxLength(128);
            if (isSqlite)
            {
                _ = nameProp.UseCollation("NOCASE");
            }

            _ = b.HasOne(m => m.Manufacturer)
             .WithMany(x => x.Models)
             .HasForeignKey(m => m.ManufacturerId)
             .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths

            // Persisted shadow column for cross-provider case-insensitive uniqueness inside a manufacturer.
            _ = b.Property<string>("NameLowered")
                .HasColumnName("NameLowered")
                .HasMaxLength(128)
                .IsRequired();
            _ = b.HasIndex(nameof(PrinterModel.ManufacturerId), "NameLowered").IsUnique();

            // Basic properties
            _ = b.Property(m => m.MotionType); // MotionType enum stored as int
            _ = b.Property(m => m.MaxX);
            _ = b.Property(m => m.MaxY);
            _ = b.Property(m => m.MaxZ);
            _ = b.Property(m => m.DefaultBackend);

            // Capability defaults (nozzle diameter and max hotend temp are now on toolheads)
            _ = b.Property(m => m.HasHeatedBed).HasDefaultValue(true);
            _ = b.Property(m => m.HasEnclosure).HasDefaultValue(false);
            _ = b.Property(m => m.MultiMaterial).HasDefaultValue(false);
            _ = b.Property(m => m.SupportsAutoLeveling).HasDefaultValue(false);
            _ = b.Property(m => m.MaxBedTemp).HasDefaultValue(120);
            _ = b.Property(m => m.MaxPrintSpeed).HasDefaultValue(150);
        });

        _ = modelBuilder.Entity<FilamentType>(b =>
        {
            _ = b.HasKey(f => f.Id);
            bool isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            PropertyBuilder<string> nameProp = b.Property(f => f.Name).IsRequired().HasMaxLength(64);
            if (isSqlite)
            {
                _ = nameProp.UseCollation("NOCASE");
            }

            _ = b.HasIndex(f => f.Name).IsUnique();
            _ = b.Property(f => f.DefaultHotendTemp);
            _ = b.Property(f => f.DefaultBedTemp);
            _ = b.Property(f => f.CreatedAt).IsRequired();
        });

        // Configure many-to-many relationship between PrinterModel and FilamentType using skip navigation
        _ = modelBuilder.Entity<PrinterModel>()
            .HasMany(p => p.SupportedFilamentTypes)
            .WithMany(f => f.PrinterModels);

        _ = modelBuilder.Entity<PrinterModelAlias>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.SlicerModelName).IsRequired().HasMaxLength(256);
            _ = b.Property(a => a.SlicerType).HasMaxLength(128);
            _ = b.Property(a => a.CreatedAt).IsRequired();
            _ = b.HasOne(a => a.PrinterModel)
             .WithMany()
             .HasForeignKey(a => a.PrinterModelId)
             .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint: SlicerModelName + SlicerType (NULL safe)
            _ = b.HasIndex(a => new { a.PrinterModelId, a.SlicerModelName, a.SlicerType }).IsUnique();
        });

        _ = modelBuilder.Entity<Spool>(b =>
        {
            _ = b.HasKey(s => s.Id);
            _ = b.Property(s => s.Material).IsRequired().HasMaxLength(64);
            _ = b.Property(s => s.ColorHex).IsRequired().HasMaxLength(16);
            _ = b.HasOne<Printer>()
             .WithMany()
             .HasForeignKey(s => s.AssignedPrinterId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        _ = modelBuilder.Entity<SpoolmanConfig>(b =>
        {
            _ = b.HasKey(c => c.Id);
            _ = b.Property(c => c.BaseUrl).IsRequired().HasMaxLength(256);
        });

        // G-code File Entity Configuration
        _ = modelBuilder.Entity<GcodeFile>(b =>
        {
            _ = b.HasKey(g => g.Id);
            _ = b.Property(g => g.FileName).IsRequired().HasMaxLength(255);
            _ = b.Property(g => g.FileHash).IsRequired().HasMaxLength(64);
            _ = b.Property(g => g.FileSizeBytes).IsRequired();
            _ = b.Property(g => g.FilePath).IsRequired().HasMaxLength(512);
            _ = b.Property(g => g.ThumbnailFileName).HasMaxLength(255); // Path to thumbnail image
            _ = b.Property(g => g.SlicerName).HasMaxLength(128);
            _ = b.Property(g => g.SlicerVersion).HasMaxLength(64);
            _ = b.Property(g => g.RequiredMaterial).HasMaxLength(64);
            _ = b.Property(g => g.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
            _ = b.Property(g => g.LastVerificationResult).HasColumnType("TEXT");

            // Foreign Keys - Use NoAction to avoid cascade conflicts in SQL Server
            _ = b.HasOne(g => g.Folder)
                .WithMany(f => f.Files)
                .HasForeignKey(g => g.FolderId)
                .OnDelete(DeleteBehavior.SetNull);
            _ = b.HasOne(g => g.SourcePrinter)
                .WithMany()
                .HasForeignKey(g => g.SourcePrinterId)
                .OnDelete(DeleteBehavior.NoAction);
            _ = b.HasOne(g => g.PrinterModel)
                .WithMany()
                .HasForeignKey(g => g.PrinterModelId)
                .OnDelete(DeleteBehavior.NoAction);

            // Navigation: GcodeFile -> Tags (skip-navigation collection)
            // Skip-navigation: GcodeFile.Tags - join table managed by EF Core
            _ = b.HasMany(g => g.Tags)
                .WithMany();

            // Indexes
            _ = b.HasIndex(g => g.FileHash).IsUnique();
            _ = b.HasIndex(g => g.UploadedAt);
            _ = b.HasIndex(g => g.FolderId); // Index for virtual directory queries
            _ = b.HasIndex(g => g.RequiredNozzleDiameter);
            _ = b.HasIndex(g => g.RequiredMaterial);
            _ = b.HasIndex(g => g.SourcePrinterId);
            _ = b.HasIndex(g => g.HealthStatus); // Index for dashboard queries
            _ = b.HasIndex(g => g.LastHealthCheckDate); // Index for recent health checks
        });

        // Harvest File to GCode File Mapping Configuration
        _ = modelBuilder.Entity<HarvestFileGcodeFileMapping>(b =>
        {
            _ = b.HasKey(m => m.Id);
            _ = b.Property(m => m.CreatedAt).IsRequired();

            // Foreign key to HarvestDiscoveredFile
            // Use Restrict (not Cascade) to prevent accidental deletion of mappings when cleaning up harvest operations
            // This protects GcodeFile records from being orphaned if someone deletes the harvest operation
            _ = b.HasOne<HarvestDiscoveredFile>()
                .WithMany(h => h.GcodeFileMappings)
                .HasForeignKey(m => m.HarvestDiscoveredFileId)
                .OnDelete(DeleteBehavior.Restrict);

            // Foreign key to GcodeFile
            // Use NoAction to absolutely prevent cascade deletion of library files from harvest operations
            _ = b.HasOne<GcodeFile>()
                .WithMany(g => g.HarvestFileMappings)
                .HasForeignKey(m => m.GcodeFileId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes for common queries
            _ = b.HasIndex(m => m.HarvestDiscoveredFileId);
            _ = b.HasIndex(m => m.GcodeFileId);
            _ = b.HasIndex(m => m.CreatedAt);
        });

        // Print Job Entity Configuration
        _ = modelBuilder.Entity<PrintJob>(b =>
        {
            _ = b.HasKey(j => j.Id);
            _ = b.Property(j => j.Name).IsRequired().HasMaxLength(255);
            _ = b.Property(j => j.Status).HasConversion<int>();
            _ = b.Property(j => j.Priority).HasDefaultValue(0);
            _ = b.Property(j => j.EstimatedPrintTime).HasConversion<long>();
            _ = b.Property(j => j.ActualPrintTime).HasConversion<long>();

            // JSON array properties
            _ = b.Property(j => j.RequiredCapabilities)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));
            _ = b.Property(j => j.PreferredPrinterIds)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<Guid[]>(v, (JsonSerializerOptions?)null));
            _ = b.Property(j => j.ExcludedPrinterIds)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<Guid[]>(v, (JsonSerializerOptions?)null));

            // Foreign Keys - Use NoAction to avoid cascade conflicts
            _ = b.HasOne(j => j.GcodeFile)
                .WithMany()
                .HasForeignKey(j => j.GcodeFileId)
                .OnDelete(DeleteBehavior.NoAction);
            _ = b.HasOne(j => j.AssignedPrinter)
                .WithMany()
                .HasForeignKey(j => j.AssignedPrinterId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes
            _ = b.HasIndex(j => j.Status);
            _ = b.HasIndex(j => j.QueuedAt);
            _ = b.HasIndex(j => j.Priority);
            _ = b.HasIndex(j => j.AssignedPrinterId);
        });

        // JobStateHistory Entity Configuration (Phase 3C)
        _ = modelBuilder.Entity<JobStateHistory>(b =>
        {
            _ = b.HasKey(h => h.Id);
            _ = b.Property(h => h.JobId).IsRequired();
            _ = b.Property(h => h.FromState).IsRequired().HasMaxLength(50);
            _ = b.Property(h => h.ToState).IsRequired().HasMaxLength(50);
            _ = b.Property(h => h.TransitionedAtUtc).IsRequired();
            _ = b.Property(h => h.DurationInState).HasConversion<long>();
            _ = b.Property(h => h.Notes).HasMaxLength(500);
            _ = b.Property(h => h.CreatedAt).IsRequired();

            // Foreign Key
            _ = b.HasOne(h => h.PrintJob)
                .WithMany(j => j.StateHistory)
                .HasForeignKey(h => h.JobId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            _ = b.HasIndex(h => h.JobId);
            _ = b.HasIndex(h => h.TransitionedAtUtc).IsDescending();
        });

        // JobSchedule Entity Configuration (Phase 4.1)
        _ = modelBuilder.Entity<JobSchedule>(b =>
        {
            _ = b.HasKey(js => js.Id);
            _ = b.Property(js => js.TimeZone).IsRequired().HasDefaultValue("UTC");
            _ = b.Property(js => js.IsActive).HasDefaultValue(true);
            _ = b.Property(js => js.IsPaused).HasDefaultValue(false);

            // Foreign Key - one-to-one relationship with PrintJob
            _ = b.HasOne(js => js.PrintJob)
                .WithOne(j => j.Schedule)
                .HasForeignKey<JobSchedule>(js => js.PrintJobId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for querying
            _ = b.HasIndex(js => js.ScheduledStartTime);
            _ = b.HasIndex(js => js.IsActive);
            _ = b.HasIndex(js => new { js.IsActive, js.IsPaused });
        });

        // JobExecution Entity Configuration (Phase 4.1)
        _ = modelBuilder.Entity<JobExecution>(b =>
        {
            _ = b.HasKey(je => je.Id);
            _ = b.Property(je => je.Status).IsRequired().HasMaxLength(50);
            _ = b.Property(je => je.Message).HasMaxLength(500);

            // Foreign Key
            _ = b.HasOne(je => je.JobSchedule)
                .WithMany(js => js.Executions)
                .HasForeignKey(je => je.JobScheduleId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for querying execution history
            _ = b.HasIndex(je => new { je.JobScheduleId, je.ScheduledExecutionTime });
            _ = b.HasIndex(je => je.Status);
            _ = b.HasIndex(je => je.ScheduledExecutionTime);
        });

        // PrintJobStatistics Entity Configuration (Phase 4.2)
        _ = modelBuilder.Entity<PrintJobStatistics>(b =>
        {
            _ = b.HasKey(s => s.Id);
            _ = b.Property(s => s.Material).HasMaxLength(100);
            _ = b.Property(s => s.FailureReason).HasMaxLength(500);
            _ = b.Property(s => s.CreatedAtUtc).IsRequired();
            _ = b.Property(s => s.UpdatedAtUtc).IsRequired();

            // Foreign Key - one-to-one relationship with PrintJob
            _ = b.HasOne(s => s.PrintJob)
                .WithOne(j => j.Statistics)
                .HasForeignKey<PrintJobStatistics>(s => s.PrintJobId)
                .OnDelete(DeleteBehavior.Cascade);

            // Foreign Key to PrinterModel (optional)
            _ = b.HasOne(s => s.PrinterModel)
                .WithMany()
                .HasForeignKey(s => s.PrinterModelId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes for prediction queries
            _ = b.HasIndex(s => s.CompletedAtUtc);
            _ = b.HasIndex(s => s.IsSuccess);
            _ = b.HasIndex(s => new { s.PrinterModelId, s.Material, s.IsSuccess });
            _ = b.HasIndex(s => new { s.PrinterModelId, s.Material, s.CompletedAtUtc });
        });

        // RetryPolicy Entity Configuration (Phase 4.4)
        _ = modelBuilder.Entity<RetryPolicy>(b =>
        {
            _ = b.HasKey(r => r.Id);
            _ = b.Property(r => r.IsEnabled).HasDefaultValue(true);
            _ = b.Property(r => r.MaxRetries).HasDefaultValue(3);
            _ = b.Property(r => r.InitialDelaySeconds).HasDefaultValue(60);
            _ = b.Property(r => r.ExponentialBase).HasDefaultValue(2.0);
            _ = b.Property(r => r.MaxDelaySeconds).HasDefaultValue(3600);
            _ = b.Property(r => r.RetryOnErrorCategories).HasMaxLength(100).HasDefaultValue("Recoverable");
            _ = b.Property(r => r.CreatedAt).IsRequired();
            _ = b.Property(r => r.UpdatedAt).IsRequired();

            // No indexes needed - typically only one global retry policy, accessed infrequently
        });

        // JobRetry Entity Configuration (Phase 4.4)
        _ = modelBuilder.Entity<JobRetry>(b =>
        {
            _ = b.HasKey(jr => jr.Id);
            _ = b.Property(jr => jr.AttemptNumber).IsRequired();
            _ = b.Property(jr => jr.ErrorCategory).HasConversion<int>();
            _ = b.Property(jr => jr.FailureReason).IsRequired().HasMaxLength(500);
            _ = b.Property(jr => jr.Status).IsRequired().HasMaxLength(50).HasDefaultValue("Pending");
            _ = b.Property(jr => jr.Notes).HasMaxLength(500);
            _ = b.Property(jr => jr.CreatedAt).IsRequired();
            _ = b.Property(jr => jr.UpdatedAt).IsRequired();

            // Foreign Keys - many-to-one relationships with PrintJobs
            _ = b.HasOne(jr => jr.OriginalJob)
                .WithMany()
                .HasForeignKey(jr => jr.OriginalJobId)
                .OnDelete(DeleteBehavior.Restrict); // Prevent deleting original job if retry exists

            _ = b.HasOne(jr => jr.RetryJob)
                .WithMany()
                .HasForeignKey(jr => jr.RetryJobId)
                .OnDelete(DeleteBehavior.Restrict); // Prevent deleting retry job if history exists

            // Indexes for querying retry history
            _ = b.HasIndex(jr => jr.OriginalJobId);
            _ = b.HasIndex(jr => jr.RetryJobId);
            _ = b.HasIndex(jr => new { jr.OriginalJobId, jr.AttemptNumber });
            _ = b.HasIndex(jr => jr.Status);
            _ = b.HasIndex(jr => jr.ScheduledRetryTime);
        });

        // Printer Capabilities Entity Configuration - REMOVED (merged into Printer entity)

        // G-code Harvest Operation Entity Configuration
        _ = modelBuilder.Entity<GcodeHarvestOperation>(b =>
        {
            _ = b.HasKey(h => h.Id);
            _ = b.Property(h => h.Status).HasConversion<int>();

            // Foreign Key
            _ = b.HasOne(h => h.Printer)
                .WithMany()
                .HasForeignKey(h => h.PrinterId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes
            _ = b.HasIndex(h => h.PrinterId);
            _ = b.HasIndex(h => h.StartedAt);
            _ = b.HasIndex(h => h.Status);
        });

        // TODO: PrintApproval entity mapping - entity not yet implemented
        // _ = modelBuilder.Entity<Farm.Infrastructure.Domain.PrintApproval>(b =>
        // {
        //     _ = b.HasKey(p => p.Id);
        //     _ = b.Property(p => p.PrintJobId).IsRequired();
        //     _ = b.Property(p => p.PrinterId);
        //     _ = b.Property(p => p.RequestedBy).HasMaxLength(128);
        //     _ = b.Property(p => p.CreatedAt).IsRequired();
        //     _ = b.HasIndex(p => p.CreatedAt);
        // });

        // HarvestDiscoveredFile Entity Configuration
        _ = modelBuilder.Entity<HarvestDiscoveredFile>(b =>
        {
            _ = b.HasKey(f => f.Id);
            _ = b.Property(f => f.HarvestOperationId).IsRequired();
            _ = b.Property(f => f.FilePath).IsRequired().HasMaxLength(512);
            _ = b.Property(f => f.FileName).IsRequired().HasMaxLength(256);
            _ = b.Property(f => f.Size).IsRequired();
            _ = b.Property(f => f.ThumbnailUrl).HasMaxLength(512);
            _ = b.Property(f => f.Status).IsRequired();
            _ = b.Property(f => f.Error).HasMaxLength(512);
            _ = b.Property(f => f.DiscoveredAt).IsRequired();
            _ = b.Property(f => f.StartedAt);
            _ = b.Property(f => f.CompletedAt);

            // Foreign Key: HarvestOperation → HarvestDiscoveredFile (one-to-many)
            // Cascade delete is appropriate here - if a harvest operation is deleted, the discovered files should be too
            // However, the mappings to GcodeFile are protected separately by Restrict delete behavior
            _ = b.HasOne(f => f.HarvestOperation)
                .WithMany(h => h.DiscoveredFiles)
                .HasForeignKey(f => f.HarvestOperationId)
                .OnDelete(DeleteBehavior.Cascade);

            _ = b.HasIndex(f => f.HarvestOperationId);
        });
        _ = modelBuilder.Entity<User>(b =>
        {
            _ = b.HasKey(u => u.Id);
            _ = b.Property(u => u.Username).IsRequired().HasMaxLength(50);
            _ = b.Property(u => u.Email).IsRequired().HasMaxLength(255);
            _ = b.Property(u => u.PasswordHash).IsRequired().HasMaxLength(255);
            _ = b.Property(u => u.FirstName).HasMaxLength(100);
            _ = b.Property(u => u.LastName).HasMaxLength(100);
            _ = b.Property(u => u.EmailConfirmationToken).HasMaxLength(255);
            _ = b.Property(u => u.PasswordResetToken).HasMaxLength(255);

            // Unique constraints
            _ = b.HasIndex(u => u.Username).IsUnique();
            _ = b.HasIndex(u => u.Email).IsUnique();
            _ = b.HasIndex(u => u.IsActive);
            _ = b.HasIndex(u => u.CreatedAt);
        });

        // Role Entity Configuration
        _ = modelBuilder.Entity<Role>(b =>
        {
            _ = b.HasKey(r => r.Id);
            _ = b.Property(r => r.Name).IsRequired().HasMaxLength(50);
            _ = b.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
            _ = b.Property(r => r.Description).HasColumnType("TEXT");

            // Unique constraints
            _ = b.HasIndex(r => r.Name).IsUnique();
            _ = b.HasIndex(r => r.IsSystemRole);
            _ = b.HasIndex(r => r.IsActive);
        });

        // Resource Entity Configuration
        _ = modelBuilder.Entity<Resource>(b =>
        {
            _ = b.HasKey(r => r.Id);
            _ = b.Property(r => r.Name).IsRequired().HasMaxLength(100);
            _ = b.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
            _ = b.Property(r => r.Description).HasColumnType("TEXT");
            _ = b.Property(r => r.ResourceType).IsRequired().HasMaxLength(50);

            // Unique constraints
            _ = b.HasIndex(r => r.Name).IsUnique();
            _ = b.HasIndex(r => r.ResourceType);
            _ = b.HasIndex(r => r.IsActive);
        });

        // UserAction Entity Configuration
        _ = modelBuilder.Entity<UserAction>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.Name).IsRequired().HasMaxLength(50);
            _ = b.Property(a => a.DisplayName).IsRequired().HasMaxLength(100);
            _ = b.Property(a => a.Description).HasColumnType("TEXT");

            // Unique constraints
            _ = b.HasIndex(a => a.Name).IsUnique();
        });

        // RolePermission Entity Configuration
        _ = modelBuilder.Entity<RolePermission>(b =>
        {
            _ = b.HasKey(rp => rp.Id);

            // Foreign Keys
            _ = b.HasOne(rp => rp.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            _ = b.HasOne(rp => rp.Resource)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.ResourceId)
                .OnDelete(DeleteBehavior.Cascade);

            _ = b.HasOne(rp => rp.Action)
                .WithMany(ua => ua.RolePermissions)
                .HasForeignKey(rp => rp.ActionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint - one permission per role-resource-action combination
            _ = b.HasIndex(rp => new { rp.RoleId, rp.ResourceId, rp.ActionId }).IsUnique();
        });

        // UserRole Entity Configuration
        _ = modelBuilder.Entity<UserRole>(b =>
        {
            _ = b.HasKey(ur => ur.Id);

            // Foreign Keys
            _ = b.HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            _ = b.HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint - one assignment per user-role combination
            _ = b.HasIndex(ur => new { ur.UserId, ur.RoleId }).IsUnique();
            _ = b.HasIndex(ur => ur.IsActive);
            _ = b.HasIndex(ur => ur.ExpiresAt);
        });

        // RefreshToken Entity Configuration
        _ = modelBuilder.Entity<RefreshToken>(b =>
        {
            _ = b.HasKey(rt => rt.Id);
            _ = b.Property(rt => rt.Token).IsRequired().HasMaxLength(512);
            _ = b.Property(rt => rt.CreatedByIp).IsRequired().HasMaxLength(45);
            _ = b.Property(rt => rt.RevokedByIp).HasMaxLength(45);
            _ = b.Property(rt => rt.ReplacedByToken).HasMaxLength(512);

            // Foreign Key
            _ = b.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            _ = b.HasIndex(rt => rt.Token).IsUnique();
            _ = b.HasIndex(rt => rt.UserId);
            _ = b.HasIndex(rt => rt.ExpiresAt);
            _ = b.HasIndex(rt => rt.IsRevoked);
        });

        _ = modelBuilder.Entity<PasswordResetToken>(b =>
        {
            _ = b.HasKey(prt => prt.Id);
            _ = b.Property(prt => prt.Token).IsRequired().HasMaxLength(256);
            _ = b.Property(prt => prt.UsedByIp).HasMaxLength(45);

            // Foreign Key
            _ = b.HasOne(prt => prt.User)
                .WithMany()
                .HasForeignKey(prt => prt.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            _ = b.HasIndex(prt => prt.Token).IsUnique();
            _ = b.HasIndex(prt => prt.UserId);
            _ = b.HasIndex(prt => prt.ExpiresAt);
            _ = b.HasIndex(prt => prt.IsUsed);
        });

        _ = modelBuilder.Entity<AuthAuditLog>(b =>
        {
            _ = b.HasKey(aal => aal.Id);
            _ = b.Property(aal => aal.EventType).IsRequired();
            _ = b.Property(aal => aal.Timestamp).IsRequired();
            _ = b.Property(aal => aal.IpAddress).HasMaxLength(45);
            _ = b.Property(aal => aal.UserAgent).HasMaxLength(512);
            _ = b.Property(aal => aal.FailureReason).HasMaxLength(512);
            _ = b.Property(aal => aal.Metadata).HasColumnType("TEXT");
            _ = b.Property(aal => aal.CorrelationId).HasMaxLength(64);

            // Foreign Key (nullable - for failed logins where user doesn't exist)
            _ = b.HasOne(aal => aal.User)
                .WithMany()
                .HasForeignKey(aal => aal.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for common queries
            _ = b.HasIndex(aal => aal.UserId);
            _ = b.HasIndex(aal => aal.EventType);
            _ = b.HasIndex(aal => aal.Timestamp);
            _ = b.HasIndex(aal => aal.Success);
            _ = b.HasIndex(aal => new { aal.UserId, aal.Timestamp }); // Common query pattern
        });

        _ = modelBuilder.Entity<RevokedToken>(b =>
        {
            _ = b.HasKey(rt => rt.Id);
            _ = b.Property(rt => rt.TokenHash).IsRequired().HasMaxLength(64); // SHA256 hash = 64 hex chars
            _ = b.Property(rt => rt.Reason).IsRequired().HasMaxLength(512);
            _ = b.Property(rt => rt.IpAddress).HasMaxLength(45);

            // Foreign Keys
            _ = b.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths in SQL Server

            _ = b.HasOne(rt => rt.RevokedByUser)
                .WithMany()
                .HasForeignKey(rt => rt.RevokedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for fast token lookup and cleanup
            _ = b.HasIndex(rt => rt.TokenHash).IsUnique(); // Fast revocation check
            _ = b.HasIndex(rt => rt.UserId); // Get all revoked tokens for a user
            _ = b.HasIndex(rt => rt.ExpiresAt); // Cleanup expired revocations
            _ = b.HasIndex(rt => rt.RevokedAt); // Audit queries
        });

        // Model3D Entity Configuration
        _ = modelBuilder.Entity<Model3D>(b =>
        {
            _ = b.HasKey(m => m.Id);
            _ = b.Property(m => m.FileName).IsRequired().HasMaxLength(255);
            _ = b.Property(m => m.FilePath).IsRequired().HasMaxLength(512);
            _ = b.Property(m => m.FileHash).IsRequired().HasMaxLength(64);
            _ = b.Property(m => m.FileFormat).HasConversion<int>();
            _ = b.Property(m => m.FileSizeBytes).IsRequired();
            _ = b.Property(m => m.ThumbnailFileName).HasMaxLength(255); // Path to thumbnail image
            _ = b.Property(m => m.ValidationErrors).HasColumnType("TEXT");
            _ = b.Property(m => m.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
            _ = b.Property(m => m.LastVerificationResult).HasColumnType("TEXT");

            // Foreign Keys
            // Foreign Keys
            _ = b.HasOne(m => m.Folder)
                .WithMany(f => f.Models)
                .HasForeignKey(m => m.FolderId)
                .OnDelete(DeleteBehavior.SetNull);
            _ = b.HasOne(m => m.UploadedByUser)
                .WithMany()
                .HasForeignKey(m => m.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Navigation: Model3D -> Tags (skip-navigation collection)
            // Skip-navigation: Model3D.Tags - join table managed by EF Core
            _ = b.HasMany(m => m.Tags)
                .WithMany();

            // Indexes
            _ = b.HasIndex(m => m.FileHash).IsUnique();
            _ = b.HasIndex(m => m.UploadedAt);
            _ = b.HasIndex(m => m.FolderId); // Index for virtual directory queries
            _ = b.HasIndex(m => m.FileFormat);
            _ = b.HasIndex(m => m.IsValid);
            _ = b.HasIndex(m => m.UploadedByUserId);
            _ = b.HasIndex(m => m.HealthStatus); // Index for dashboard queries
            _ = b.HasIndex(m => m.LastHealthCheckDate); // Index for recent health checks
        });

        // Tag Entity Configuration (Generic tag for all object types)
        _ = modelBuilder.Entity<Tag>(b =>
        {
            _ = b.HasKey(t => t.Id);
            _ = b.Property(t => t.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(t => t.Color).HasMaxLength(7); // Hex color codes
            _ = b.Property(t => t.Description).HasMaxLength(512);

            // Index for quick tag lookups
            _ = b.HasIndex(t => t.Name).IsUnique();

            // Index for analytics
            _ = b.HasIndex(t => t.CreatedAt);
        });

        // FolderNode Entity Configuration
        _ = modelBuilder.Entity<FolderNode>(b =>
        {
            _ = b.HasKey(f => f.Id);
            _ = b.Property(f => f.Path).IsRequired().HasMaxLength(1024);
            _ = b.Property(f => f.FolderType).IsRequired().HasMaxLength(50);
            _ = b.Property(f => f.CreatedAt).IsRequired();

            // Navigation: FolderNode -> Models (inverse of Model3D.Folder)
            _ = b.HasMany(f => f.Models)
                .WithOne(m => m.Folder)
                .HasForeignKey(m => m.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            // Navigation: FolderNode -> Files (inverse of GcodeFile.Folder)
            _ = b.HasMany(f => f.Files)
                .WithOne(g => g.Folder)
                .HasForeignKey(g => g.FolderId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            _ = b.HasIndex(f => new { f.Path, f.FolderType }).IsUnique();
        });

        // ProcessProfile Entity Configuration
        _ = modelBuilder.Entity<ProcessProfile>(b =>
        {
            _ = b.HasKey(p => p.Id);
            _ = b.Property(p => p.Name).IsRequired().HasMaxLength(255);
            _ = b.Property(p => p.Description).HasMaxLength(1000);
            _ = b.Property(p => p.SlicerType).HasConversion<int>();
            _ = b.Property(p => p.Quality).HasConversion<int>();
            _ = b.Property(p => p.AdvancedSettings).HasColumnType("TEXT");
            _ = b.Property(p => p.RawJson).HasColumnType("TEXT");
            _ = b.Property(p => p.SettingsJson).HasColumnType("TEXT");
            _ = b.Property(p => p.Hash).HasMaxLength(64);
            _ = b.Property(p => p.IsSystem).HasDefaultValue(false);

            // Foreign Keys
            _ = b.HasOne(p => p.PrinterModel)
                .WithMany()
                .HasForeignKey(p => p.PrinterModelId)
                .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(p => p.SpecificPrinter)
                .WithMany()
                .HasForeignKey(p => p.SpecificPrinterId)
                .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(p => p.CreatedByUser)
                .WithMany()
                .HasForeignKey(p => p.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            _ = b.HasIndex(p => new { p.Name, p.SlicerType, p.PrinterModelId }).IsUnique();
            _ = b.HasIndex(p => p.SlicerType);
            _ = b.HasIndex(p => p.PrinterModelId);
            _ = b.HasIndex(p => p.IsDefault);
            _ = b.HasIndex(p => p.IsPublic);
            _ = b.HasIndex(p => p.CreatedByUserId);
            _ = b.HasIndex(p => p.Hash).IsUnique();
            _ = b.HasIndex(p => p.IsSystem);
        });

        // MachineProfile Entity Configuration
        _ = modelBuilder.Entity<MachineProfile>(b =>
        {
            _ = b.HasKey(p => p.Id);
            _ = b.Property(p => p.Name).IsRequired().HasMaxLength(255);
            _ = b.Property(p => p.Manufacturer).IsRequired().HasMaxLength(255);
            _ = b.Property(p => p.Description).HasMaxLength(1000);
            _ = b.Property(p => p.SlicerType).HasConversion<int>();
            _ = b.Property(p => p.RawJson).HasColumnType("TEXT");
            _ = b.Property(p => p.SettingsJson).HasColumnType("TEXT");
            _ = b.Property(p => p.Hash).HasMaxLength(64);
            _ = b.Property(p => p.IsSystem).HasDefaultValue(false);

            // Foreign Keys
            _ = b.HasOne(p => p.PrinterModel)
                .WithMany()
                .HasForeignKey(p => p.PrinterModelId)
                .OnDelete(DeleteBehavior.SetNull);

            _ = b.HasOne(p => p.CreatedByUser)
                .WithMany()
                .HasForeignKey(p => p.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            _ = b.HasIndex(p => new { p.Name, p.SlicerType }).IsUnique();
            _ = b.HasIndex(p => p.SlicerType);
            _ = b.HasIndex(p => p.Manufacturer);
            _ = b.HasIndex(p => p.Hash).IsUnique();
            _ = b.HasIndex(p => p.IsSystem);
            _ = b.HasIndex(p => p.CreatedByUserId);
        });

        // FilamentProfile Entity Configuration
        _ = modelBuilder.Entity<FilamentProfile>(b =>
        {
            _ = b.HasKey(p => p.Id);
            _ = b.Property(p => p.Name).IsRequired().HasMaxLength(255);
            _ = b.Property(p => p.Material).IsRequired().HasMaxLength(64);
            _ = b.Property(p => p.Manufacturer).HasMaxLength(255);
            _ = b.Property(p => p.Description).HasMaxLength(1000);
            _ = b.Property(p => p.SlicerType).HasConversion<int>();
            _ = b.Property(p => p.RawJson).HasColumnType("TEXT");
            _ = b.Property(p => p.SettingsJson).HasColumnType("TEXT");
            _ = b.Property(p => p.Hash).HasMaxLength(64);
            _ = b.Property(p => p.IsSystem).HasDefaultValue(false);

            // Foreign Keys
            _ = b.HasOne(p => p.CreatedByUser)
                .WithMany()
                .HasForeignKey(p => p.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes - Name included in unique constraint to allow multiple profiles with same material
            // (e.g., "Generic PLA" vs "Bambu PLA" both with Material="PLA")
            _ = b.HasIndex(p => new { p.Name, p.Material, p.SlicerType }).IsUnique();
            _ = b.HasIndex(p => p.SlicerType);
            _ = b.HasIndex(p => p.Material);
            _ = b.HasIndex(p => p.Hash).IsUnique();
            _ = b.HasIndex(p => p.IsSystem);
            _ = b.HasIndex(p => p.CreatedByUserId);
        });

        // Slicer Service (Registry) Entity Configuration
        _ = modelBuilder.Entity<SlicerService>(b =>
        {
            _ = b.HasKey(s => s.Id);
            _ = b.Property(s => s.Name).IsRequired().HasMaxLength(200);
            _ = b.Property(s => s.Version).HasMaxLength(64);
            _ = b.Property(s => s.Host).HasMaxLength(512);
            _ = b.Property(s => s.UiManifestUrl).HasMaxLength(512);
            _ = b.Property(s => s.CapabilitiesJson).HasColumnType("TEXT");
            _ = b.Property(s => s.Status).HasMaxLength(64);
            _ = b.Property(s => s.ApiKey).HasMaxLength(128);
            _ = b.HasIndex(s => s.Name);
            _ = b.HasIndex(s => s.SlicerType);
            _ = b.HasIndex(s => s.Status);
        });

        // SliceJob Entity Configuration
        _ = modelBuilder.Entity<SliceJob>(b =>
        {
            _ = b.HasKey(j => j.Id);
            _ = b.Property(j => j.UserId).IsRequired();
            _ = b.Property(j => j.ModelFileUrl).IsRequired().HasMaxLength(2048);
            _ = b.Property(j => j.ModelFileName).IsRequired().HasMaxLength(512);
            _ = b.Property(j => j.SlicerEngine).IsRequired();
            _ = b.Property(j => j.SlicerProfileJson).HasColumnType("TEXT");
            _ = b.Property(j => j.SlicerProfileId);
            _ = b.Property(j => j.RequiredCapabilitiesJson).HasColumnType("TEXT");
            _ = b.Property(j => j.Status).IsRequired().HasMaxLength(50);
            _ = b.Property(j => j.Priority).IsRequired();
            _ = b.Property(j => j.QueuedAt).IsRequired();
            _ = b.Property(j => j.ResultFileUrl).HasMaxLength(2048);
            _ = b.Property(j => j.ErrorMessage).HasColumnType("TEXT");
            _ = b.Property(j => j.ProgressMessage).HasMaxLength(512);
            _ = b.Property(j => j.CreatedAt).IsRequired();
            _ = b.Property(j => j.UpdatedAt).IsRequired();

            // Indexes for efficient querying
            _ = b.HasIndex(j => j.UserId);
            _ = b.HasIndex(j => j.PrinterId);
            _ = b.HasIndex(j => j.Status);
            _ = b.HasIndex(j => j.QueuedAt);
            _ = b.HasIndex(j => new { j.Status, j.Priority, j.QueuedAt }); // For queue processing
            _ = b.HasIndex(j => j.WorkerId);
            _ = b.HasIndex(j => j.SlicerProfileId);

            // Foreign key to SlicerProfile (optional reference). If profile deleted later we retain immutable snapshot JSON.
            _ = b.HasOne(j => j.SlicerProfile)
                .WithMany()
                .HasForeignKey(j => j.SlicerProfileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Artifact Entity Configuration
        _ = modelBuilder.Entity<Artifact>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.JobId).IsRequired();
            _ = b.Property(a => a.Kind).IsRequired().HasMaxLength(64);
            _ = b.Property(a => a.FileName).IsRequired().HasMaxLength(256);
            _ = b.Property(a => a.RelativePath).IsRequired().HasMaxLength(1024);
            _ = b.Property(a => a.ContentType).IsRequired().HasMaxLength(128);
            _ = b.Property(a => a.SizeBytes).IsRequired();
            _ = b.Property(a => a.Sha256).IsRequired().HasMaxLength(64);
            _ = b.Property(a => a.CreatedAt).IsRequired();

            // Helpful indexes for lookup & listing
            _ = b.HasIndex(a => a.JobId);
            _ = b.HasIndex(a => a.WorkerId);
            _ = b.HasIndex(a => a.CreatedAt);
            _ = b.HasIndex(a => new { a.JobId, a.Kind });
        });

        // Worker Entity Configuration
        _ = modelBuilder.Entity<Worker>(b =>
        {
            _ = b.HasKey(w => w.Id);
            _ = b.Property(w => w.ServiceId).IsRequired().HasMaxLength(256);
            _ = b.Property(w => w.Name).IsRequired().HasMaxLength(256);
            _ = b.Property(w => w.EndpointUrl).IsRequired().HasMaxLength(2048);
            _ = b.Property(w => w.CapabilitiesJson).HasColumnType("TEXT");
            _ = b.Property(w => w.Status).IsRequired().HasMaxLength(50);
            _ = b.Ignore(w => w.FreeSlots); // FreeSlots is calculated property
            _ = b.Property(w => w.TotalSlots).IsRequired();
            _ = b.Property(w => w.RegisteredAt).IsRequired();
            _ = b.Property(w => w.ApiKey).HasMaxLength(512);
            _ = b.Property(w => w.Version).HasMaxLength(50);
            _ = b.Property(w => w.MetadataJson).HasColumnType("TEXT");
            _ = b.Property(w => w.CreatedAt).IsRequired();
            _ = b.Property(w => w.UpdatedAt).IsRequired();
            _ = b.Property(w => w.DisabledReason).HasMaxLength(1024);

            // Indexes for efficient querying
            _ = b.HasIndex(w => w.ServiceId).IsUnique();
            _ = b.HasIndex(w => w.Status);
            _ = b.HasIndex(w => w.LastHeartbeat);
        });

        _ = modelBuilder.Entity<PasswordPolicyEntity>(b =>
        {
            // Keep the existing table name to avoid creating a migration due to the rename
            _ = b.ToTable("PasswordPolicies");
            _ = b.HasKey(pp => pp.Id);
            _ = b.Property(pp => pp.MinLength).IsRequired();
            _ = b.Property(pp => pp.RequireUppercase);
            _ = b.Property(pp => pp.RequireLowercase);
            _ = b.Property(pp => pp.RequireDigit);
            _ = b.Property(pp => pp.RequireSymbol);
        });

        // SlicerSettings Entity Configuration
        _ = modelBuilder.Entity<SlicerSettings>(b =>
        {
            _ = b.HasKey(s => s.Id);
            _ = b.Property(s => s.Enabled).IsRequired();
            _ = b.Property(s => s.PerEngineJson).HasColumnType("TEXT");
            _ = b.Property(s => s.UpdatedAt).IsRequired();
            _ = b.Property(s => s.JitterPercent).HasDefaultValue(15.0).IsRequired();
        });

        // HarvestDiscoveredFile Entity Configuration
        _ = modelBuilder.Entity<HarvestDiscoveredFile>(b =>
        {
            _ = b.HasKey(f => f.Id);
            _ = b.Property(f => f.HarvestOperationId).IsRequired();
            _ = b.Property(f => f.FilePath).IsRequired().HasMaxLength(512);
            _ = b.Property(f => f.FileName).IsRequired().HasMaxLength(256);
            _ = b.Property(f => f.Size).IsRequired();
            _ = b.Property(f => f.ThumbnailUrl).HasMaxLength(512);
            _ = b.Property(f => f.Status).IsRequired();
            _ = b.Property(f => f.Error).HasMaxLength(512);
            _ = b.Property(f => f.DiscoveredAt).IsRequired();
            _ = b.Property(f => f.StartedAt);
            _ = b.Property(f => f.CompletedAt);
            _ = b.HasIndex(f => f.HarvestOperationId);
        });

        // GcodeHarvestQueueItem Entity Configuration
        _ = modelBuilder.Entity<GcodeHarvestQueueItem>(b =>
        {
            _ = b.HasKey(q => q.Id);
            _ = b.Property(q => q.PrinterId).IsRequired();
            _ = b.Property(q => q.QueuedAt).IsRequired();
            _ = b.Property(q => q.ProcessingStartedAt);
            _ = b.Property(q => q.CompletedAt);
            _ = b.Property(q => q.Priority).IsRequired().HasDefaultValue(0);
            _ = b.Property(q => q.Status).IsRequired().HasConversion<int>(); // Pending - default set via entity initializer
            _ = b.Property(q => q.Parameters).IsRequired().HasColumnType("TEXT"); // JSON serialized parameters
            _ = b.Property(q => q.ErrorMessage);
            _ = b.Property(q => q.ErrorDetails).HasColumnType("TEXT");
            _ = b.Property(q => q.FilesFound).HasDefaultValue(0);
            _ = b.Property(q => q.FilesAdded).HasDefaultValue(0);
            _ = b.Property(q => q.FilesSkipped).HasDefaultValue(0);
            _ = b.Property(q => q.FilesErrored).HasDefaultValue(0);

            // Foreign Keys
            _ = b.HasOne(q => q.Printer)
                .WithMany()
                .HasForeignKey(q => q.PrinterId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for efficient queue processing
            _ = b.HasIndex(q => new { q.Status, q.Priority, q.QueuedAt }); // Get next item to process
            _ = b.HasIndex(q => q.PrinterId); // Find queue items for a printer
            _ = b.HasIndex(q => q.QueuedAt).IsDescending(); // Recent items first
            _ = b.HasIndex(q => q.Status); // Filter by status
        });

        // File Health Audit Entity Configuration
        _ = modelBuilder.Entity<FileHealthAudit>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.AuditDate).IsRequired();
            _ = b.Property(a => a.AuditType).HasConversion<int>();
            _ = b.Property(a => a.FilesChecked).IsRequired();
            _ = b.Property(a => a.HealthyFiles).IsRequired();
            _ = b.Property(a => a.MissingFiles).IsRequired();
            _ = b.Property(a => a.CorruptedFiles).IsRequired();
            _ = b.Property(a => a.OrphanedFiles).IsRequired();
            _ = b.Property(a => a.MissingFileIds).HasColumnType("TEXT"); // JSON array
            _ = b.Property(a => a.CorruptedFileIds).HasColumnType("TEXT"); // JSON array
            _ = b.Property(a => a.OrphanedFilePaths).HasColumnType("TEXT"); // JSON array
            _ = b.Property(a => a.SummaryMessage).HasColumnType("TEXT");
            _ = b.Property(a => a.HasIssues).IsRequired();
            _ = b.Property(a => a.CreatedAt).IsRequired();

            // Indexes for efficient querying and dashboard
            _ = b.HasIndex(a => a.AuditDate).IsDescending(); // Most recent audits first
            _ = b.HasIndex(a => a.AuditType);
            _ = b.HasIndex(a => a.HasIssues);
            _ = b.HasIndex(a => new { a.AuditType, a.AuditDate }).IsDescending(false, true); // Composite for type+recent queries
        });

        // Notification Entity Configuration (Phase 4.3)
        _ = modelBuilder.Entity<Notification>(b =>
        {
            _ = b.HasKey(n => n.Id);
            _ = b.Property(n => n.UserId).IsRequired();
            _ = b.Property(n => n.Type).IsRequired();
            _ = b.Property(n => n.Subject).IsRequired().HasMaxLength(255);
            _ = b.Property(n => n.Body).IsRequired().HasColumnType("TEXT");
            _ = b.Property(n => n.IsRead).IsRequired().HasDefaultValue(false);
            _ = b.Property(n => n.CreatedAt).IsRequired();

            // Foreign Keys
            _ = b.HasOne(n => n.User)
                .WithMany()
                .HasForeignKey(n => n.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            _ = b.HasOne(n => n.Job)
                .WithMany()
                .HasForeignKey(n => n.JobId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for efficient querying
            _ = b.HasIndex(n => n.UserId);
            _ = b.HasIndex(n => new { n.UserId, n.IsRead });
            _ = b.HasIndex(n => n.Type);
            _ = b.HasIndex(n => n.JobId);
            _ = b.HasIndex(n => n.CreatedAt).IsDescending(); // Most recent first
            _ = b.HasIndex(n => n.ExpiresAt); // For cleanup queries
        });

        // NotificationPreferences Entity Configuration (Phase 4.3)
        _ = modelBuilder.Entity<NotificationPreferences>(b =>
        {
            _ = b.HasKey(np => np.Id);
            _ = b.Property(np => np.UserId).IsRequired();
            _ = b.Property(np => np.Frequency).IsRequired().HasDefaultValue(NotificationFrequency.RealTime);
            _ = b.Property(np => np.RetentionDays).IsRequired().HasDefaultValue(30);
            _ = b.Property(np => np.UpdatedAt).IsRequired();

            // Foreign Key - one-to-one relationship with User
            _ = b.HasOne(np => np.User)
                .WithOne()
                .HasForeignKey<NotificationPreferences>(np => np.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint - one preferences per user
            _ = b.HasIndex(np => np.UserId).IsUnique();
        });

        // ApiKey Entity Configuration (OctoPrint API)
        _ = modelBuilder.Entity<ApiKey>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.UserId).IsRequired(false); // Nullable for global keys
            _ = b.Property(a => a.Name).IsRequired().HasMaxLength(256);
            _ = b.Property(a => a.KeyHash).IsRequired().HasMaxLength(64); // SHA256 hex = 64 chars
            _ = b.Property(a => a.IsActive).IsRequired().HasDefaultValue(true);
            _ = b.Property(a => a.CreatedAt).IsRequired();
            _ = b.Property(a => a.ExpiresAt).IsRequired(false);

            // Indexes for efficient querying
            _ = b.HasIndex(a => a.KeyHash).IsUnique(); // Fast lookup by hash
            _ = b.HasIndex(a => a.UserId); // Find user's keys
            _ = b.HasIndex(a => new { a.UserId, a.IsActive }); // Active keys for user
        });

        // PrintApproval Entity Configuration (OctoPrint API Upload+Print)
        _ = modelBuilder.Entity<PrintApproval>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.PrintJobId).IsRequired();
            _ = b.Property(a => a.PrinterId).IsRequired(false);
            _ = b.Property(a => a.RequestedBy).HasMaxLength(256);
            _ = b.Property(a => a.CreatedAt).IsRequired();

            // Foreign keys
            _ = b.HasOne<PrintJob>()
                .WithMany()
                .HasForeignKey(a => a.PrintJobId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for efficient querying
            _ = b.HasIndex(a => a.PrintJobId);
            _ = b.HasIndex(a => a.CreatedAt).IsDescending(); // Most recent first
        });

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
            }
        }
    }

    #region Component Model Definition Configurations

    private void ConfigureHotendModelDefinition(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<HotendModelDefinition>(b =>
        {
            _ = b.HasKey(h => h.Id);
            _ = b.Property(h => h.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(h => h.Description).HasMaxLength(512);
            _ = b.Property(h => h.MaxTemp).HasDefaultValue(300);
            _ = b.Property(h => h.IsHighFlow).HasDefaultValue(false);

            // Foreign Key to Manufacturer
            _ = b.HasOne(h => h.Manufacturer)
             .WithMany()
             .HasForeignKey(h => h.ManufacturerId)
             .OnDelete(DeleteBehavior.Restrict);

            // Index for lookups
            _ = b.HasIndex(h => h.ManufacturerId);
            _ = b.HasIndex(h => h.Name);
        });
    }

    private void ConfigureExtruderModelDefinition(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<ExtruderModelDefinition>(b =>
        {
            _ = b.HasKey(e => e.Id);
            _ = b.Property(e => e.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(e => e.Description).HasMaxLength(512);
            _ = b.Property(e => e.GearRatio).HasMaxLength(32);
            _ = b.Property(e => e.IsDirectDrive).HasDefaultValue(true);

            // Foreign Key to Manufacturer
            _ = b.HasOne(e => e.Manufacturer)
             .WithMany()
             .HasForeignKey(e => e.ManufacturerId)
             .OnDelete(DeleteBehavior.Restrict);

            // Index for lookups
            _ = b.HasIndex(e => e.ManufacturerId);
            _ = b.HasIndex(e => e.Name);
        });
    }

    private void ConfigureToolheadModelDefinition(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<ToolheadModelDefinition>(b =>
        {
            _ = b.HasKey(t => t.Id);
            _ = b.Property(t => t.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(t => t.Description).HasMaxLength(512);

            // Foreign Key to Manufacturer (nullable - community designs may not have a manufacturer)
            _ = b.HasOne(t => t.Manufacturer)
             .WithMany()
             .HasForeignKey(t => t.ManufacturerId)
             .OnDelete(DeleteBehavior.SetNull);

            // Index for lookups
            _ = b.HasIndex(t => t.ManufacturerId);
            _ = b.HasIndex(t => t.Name);
        });
    }

    private void ConfigureNozzleModelDefinition(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<NozzleModelDefinition>(b =>
        {
            _ = b.HasKey(n => n.Id);
            _ = b.Property(n => n.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(n => n.Description).HasMaxLength(512);
            _ = b.Property(n => n.MaxTemp).HasDefaultValue(500);

            // IsHardened is a computed property marked [NotMapped] - do not configure it here

            // Foreign Key to Manufacturer
            _ = b.HasOne(n => n.Manufacturer)
             .WithMany()
             .HasForeignKey(n => n.ManufacturerId)
             .OnDelete(DeleteBehavior.Restrict);

            // Index for lookups
            _ = b.HasIndex(n => n.ManufacturerId);
            _ = b.HasIndex(n => n.Name);
        });
    }

    #region Maintenance Module Configuration

    private void ConfigurePrinterStatistics(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<PrinterStatistics>(b =>
        {
            _ = b.HasKey(s => s.Id);

            // One-to-one with Printer (PrinterId should match Id)
            _ = b.HasOne(s => s.Printer)
             .WithOne()
             .HasForeignKey<PrinterStatistics>(s => s.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);

            // Index for efficient queries
            _ = b.HasIndex(s => s.PrinterId).IsUnique();
            _ = b.HasIndex(s => s.LastSyncTime);
        });
    }

    private void ConfigureMaintenanceSchedule(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<MaintenanceSchedule>(b =>
        {
            _ = b.HasKey(s => s.Id);
            _ = b.Property(s => s.TaskName).IsRequired().HasMaxLength(128);
            _ = b.Property(s => s.Description).HasMaxLength(512);
            _ = b.Property(s => s.Component).HasMaxLength(64);

            // Relationship with Printer (optional - null for model-wide defaults)
            _ = b.HasOne(s => s.Printer)
             .WithMany()
             .HasForeignKey(s => s.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);

            // Relationship with PrinterModel (optional - for model-wide defaults)
            _ = b.HasOne(s => s.PrinterModel)
             .WithMany()
             .HasForeignKey(s => s.PrinterModelId)
             .OnDelete(DeleteBehavior.Cascade);

            // Indexes for efficient queries
            _ = b.HasIndex(s => s.PrinterId);
            _ = b.HasIndex(s => s.PrinterModelId);
            _ = b.HasIndex(s => new { s.IsActive, s.IsDefault });
        });
    }

    private void ConfigureMaintenanceLog(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<MaintenanceLog>(b =>
        {
            _ = b.HasKey(l => l.Id);
            _ = b.Property(l => l.TaskName).IsRequired().HasMaxLength(128);
            _ = b.Property(l => l.Notes).HasMaxLength(2000);
            _ = b.Property(l => l.Component).HasMaxLength(64);
            _ = b.Property(l => l.PerformedBy).HasMaxLength(128);
            _ = b.Property(l => l.PartsReplaced).HasMaxLength(512);

            // Relationship with Printer (required)
            _ = b.HasOne(l => l.Printer)
             .WithMany()
             .HasForeignKey(l => l.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);

            // Relationship with MaintenanceSchedule (optional)
            _ = b.HasOne(l => l.MaintenanceSchedule)
             .WithMany()
             .HasForeignKey(l => l.MaintenanceScheduleId)
             .OnDelete(DeleteBehavior.SetNull);

            // Relationship with MaintenanceAlert (optional)
            _ = b.HasOne(l => l.ResolvedAlert)
             .WithMany()
             .HasForeignKey(l => l.ResolvedAlertId)
             .OnDelete(DeleteBehavior.SetNull);

            // Indexes for efficient queries
            _ = b.HasIndex(l => l.PrinterId);
            _ = b.HasIndex(l => l.MaintenanceScheduleId);
            _ = b.HasIndex(l => l.ResolvedAlertId);
            _ = b.HasIndex(l => l.PerformedAt);
        });
    }

    private void ConfigureMaintenanceAlert(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<MaintenanceAlert>(b =>
        {
            _ = b.HasKey(a => a.Id);
            _ = b.Property(a => a.Title).IsRequired().HasMaxLength(128);
            _ = b.Property(a => a.Message).IsRequired().HasMaxLength(512);
            _ = b.Property(a => a.AcknowledgedBy).HasMaxLength(128);
            _ = b.Property(a => a.ResolvedBy).HasMaxLength(128);
            _ = b.Property(a => a.DismissedBy).HasMaxLength(128);
            _ = b.Property(a => a.DismissalReason).HasMaxLength(512);

            // Relationship with Printer (required)
            _ = b.HasOne(a => a.Printer)
             .WithMany()
             .HasForeignKey(a => a.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);

            // Relationship with MaintenanceSchedule (required)
            _ = b.HasOne(a => a.MaintenanceSchedule)
             .WithMany()
             .HasForeignKey(a => a.MaintenanceScheduleId)
             .OnDelete(DeleteBehavior.Cascade);

            // Indexes for efficient queries
            _ = b.HasIndex(a => a.PrinterId);
            _ = b.HasIndex(a => a.MaintenanceScheduleId);
            _ = b.HasIndex(a => new { a.Status, a.Severity });
            _ = b.HasIndex(a => a.CreatedAt);
        });
    }

    #endregion

    #endregion
}
