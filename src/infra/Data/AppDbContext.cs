using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppSettingsEntity> AppSettingsEntities => Set<AppSettingsEntity>();
    public DbSet<SystemLog> SystemLogs => Set<SystemLog>();
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<Spool> Spools => Set<Spool>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<PrinterModel> Models => Set<PrinterModel>();
    public DbSet<FilamentType> FilamentTypes => Set<FilamentType>();
    public DbSet<PrinterModelFilamentType> PrinterModelFilamentTypes => Set<PrinterModelFilamentType>();
    public DbSet<SpoolmanConfig> SpoolmanConfigs => Set<SpoolmanConfig>();

    // G-code Library & Job Queue
    public DbSet<GcodeFile> GcodeFiles => Set<GcodeFile>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<Toolhead> Toolheads => Set<Toolhead>();
    public DbSet<GcodeHarvestOperation> GcodeHarvestOperations => Set<GcodeHarvestOperation>();
    public DbSet<HarvestDiscoveredFile> HarvestDiscoveredFiles => Set<HarvestDiscoveredFile>();

    // 3D Model Management & Slicer Integration
    public DbSet<Model3D> Models3D => Set<Model3D>();
    public DbSet<Model3DTag> Model3DTags => Set<Model3DTag>();
    public DbSet<Model3DTagMapping> Model3DTagMappings => Set<Model3DTagMapping>();
    public DbSet<ProcessProfile> ProcessProfiles => Set<ProcessProfile>();
    public DbSet<MachineProfile> MachineProfiles => Set<MachineProfile>();
    public DbSet<FilamentProfile> FilamentProfiles => Set<FilamentProfile>();
    public DbSet<SlicerSettings> SlicerSettings => Set<SlicerSettings>();
    public DbSet<SlicerService> SlicerServices => Set<SlicerService>();
    public DbSet<SliceJob> SliceJobs => Set<SliceJob>();
    public DbSet<Worker> Workers => Set<Worker>();
    // Slicing artifacts (G-code outputs, thumbnails, logs, previews)
    public DbSet<Artifact> Artifacts => Set<Artifact>();

    // File Health & Consistency Auditing
    public DbSet<FileHealthAudit> FileHealthAudits => Set<FileHealthAudit>();

    // User Management & Authentication
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Domain.Action> Actions => Set<Domain.Action>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<PasswordPolicyEntity> PasswordPolicies => Set<PasswordPolicyEntity>();
    public DbSet<FailedLoginAttempt> FailedLoginAttempts => Set<FailedLoginAttempt>();
    public DbSet<AuthAuditLog> AuthAuditLogs => Set<AuthAuditLog>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

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
            _ = b.Property(p => p.DateAcquired);
            
            // Toolheads collection - one printer can have multiple hotends
            _ = b.HasMany(p => p.Toolheads)
             .WithOne(t => t.Printer)
             .HasForeignKey(t => t.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Toolhead Entity Configuration
        _ = modelBuilder.Entity<Toolhead>(b =>
        {
            _ = b.HasKey(t => t.Id);
            _ = b.Property(t => t.Name).HasMaxLength(128);
            _ = b.Property(t => t.Index).IsRequired();
            _ = b.Property(t => t.NozzleDiameter).IsRequired();
            _ = b.Property(t => t.MinHotendTemp).HasDefaultValue(0);
            _ = b.Property(t => t.MaxHotendTemp).HasDefaultValue(300);
            _ = b.Property(t => t.HasHeatedEnclosure).HasDefaultValue(false);
            _ = b.Property(t => t.IsPrimary).HasDefaultValue(false);
            _ = b.Property(t => t.UpdatedAt).IsRequired();
            
            // JSON array properties
            _ = b.Property(t => t.SupportedMaterials)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));
            
            // Foreign Key
            _ = b.HasOne(t => t.Printer)
             .WithMany(p => p.Toolheads)
             .HasForeignKey(t => t.PrinterId)
             .OnDelete(DeleteBehavior.Cascade);
            
            // Indexes
            _ = b.HasIndex(t => t.PrinterId);
            _ = b.HasIndex(t => t.Index);
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

            // Capability defaults
            _ = b.Property(m => m.DefaultNozzleDiameter).HasDefaultValue(0.4);
            _ = b.Property(m => m.HasHeatedBed).HasDefaultValue(true);
            _ = b.Property(m => m.HasEnclosure).HasDefaultValue(false);
            _ = b.Property(m => m.MultiMaterial).HasDefaultValue(false);
            _ = b.Property(m => m.NumberOfExtruders).HasDefaultValue(1);
            _ = b.Property(m => m.SupportsAutoLeveling).HasDefaultValue(false);
            _ = b.Property(m => m.MinHotendTemp).HasDefaultValue(0);
            _ = b.Property(m => m.MaxHotendTemp).HasDefaultValue(300);
            _ = b.Property(m => m.MinBedTemp).HasDefaultValue(0);
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

        _ = modelBuilder.Entity<PrinterModelFilamentType>(b =>
        {
            _ = b.HasKey(pf => new { pf.PrinterModelId, pf.FilamentTypeId });
            _ = b.HasOne(pf => pf.PrinterModel)
             .WithMany(p => p.SupportedFilamentTypes)
             .HasForeignKey(pf => pf.PrinterModelId)
             .OnDelete(DeleteBehavior.Cascade);
            _ = b.HasOne(pf => pf.FilamentType)
             .WithMany(f => f.PrinterModels)
             .HasForeignKey(pf => pf.FilamentTypeId)
             .OnDelete(DeleteBehavior.Cascade);
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
            _ = b.Property(g => g.OriginalFileName).IsRequired().HasMaxLength(255);
            _ = b.Property(g => g.DisplayName).IsRequired().HasMaxLength(255);
            _ = b.Property(g => g.FileDirectory).IsRequired().HasMaxLength(512);
            _ = b.Property(g => g.FileHash).IsRequired().HasMaxLength(64);
            _ = b.Property(g => g.FileSizeBytes).IsRequired();
            _ = b.Property(g => g.FilePath).IsRequired().HasMaxLength(512);
            _ = b.Property(g => g.SlicerName).HasMaxLength(128);
            _ = b.Property(g => g.SlicerVersion).HasMaxLength(64);
            _ = b.Property(g => g.RequiredMaterial).HasMaxLength(64);
            _ = b.Property(g => g.SlicerSettings).HasColumnType("TEXT");
            _ = b.Property(g => g.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
            _ = b.Property(g => g.LastVerificationResult).HasColumnType("TEXT");

            // JSON array properties
            _ = b.Property(g => g.CompatibleMaterials)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));
            _ = b.Property(g => g.PrintTemperatures)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<double[]>(v, (JsonSerializerOptions?)null));
            _ = b.Property(g => g.TargetPrinterModels)
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));

            // Foreign Keys - Use NoAction to avoid cascade conflicts in SQL Server
            _ = b.HasOne(g => g.SourcePrinter)
                .WithMany()
                .HasForeignKey(g => g.SourcePrinterId)
                .OnDelete(DeleteBehavior.NoAction);
            _ = b.HasOne(g => g.TargetPrinter)
                .WithMany()
                .HasForeignKey(g => g.TargetPrinterId)
                .OnDelete(DeleteBehavior.NoAction);
            _ = b.HasOne(g => g.TargetModel)
                .WithMany()
                .HasForeignKey(g => g.TargetModelId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes
            _ = b.HasIndex(g => g.FileHash).IsUnique();
            _ = b.HasIndex(g => g.UploadedAt);
            _ = b.HasIndex(g => g.RequiredNozzleDiameter);
            _ = b.HasIndex(g => g.RequiredMaterial);
            _ = b.HasIndex(g => g.TargetPrinterId);
            _ = b.HasIndex(g => g.SourcePrinterId);
            _ = b.HasIndex(g => g.HealthStatus); // Index for dashboard queries
            _ = b.HasIndex(g => g.LastHealthCheckDate); // Index for recent health checks
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

        // User Entity Configuration
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

        // Action Entity Configuration
        _ = modelBuilder.Entity<Domain.Action>(b =>
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
                .WithMany(a => a.RolePermissions)
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
            _ = b.Property(m => m.OriginalFileName).IsRequired().HasMaxLength(255);
            _ = b.Property(m => m.DisplayName).IsRequired().HasMaxLength(255);
            _ = b.Property(m => m.FileDirectory).IsRequired().HasMaxLength(512);
            _ = b.Property(m => m.FilePath).IsRequired().HasMaxLength(512);
            _ = b.Property(m => m.FileHash).IsRequired().HasMaxLength(64);
            _ = b.Property(m => m.FileFormat).HasConversion<int>();
            _ = b.Property(m => m.FileSizeBytes).IsRequired();
            _ = b.Property(m => m.ValidationErrors).HasColumnType("TEXT");
            _ = b.Property(m => m.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
            _ = b.Property(m => m.LastVerificationResult).HasColumnType("TEXT");

            // Foreign Key
            _ = b.HasOne(m => m.UploadedByUser)
                .WithMany()
                .HasForeignKey(m => m.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Navigation: Model3D -> TagMappings
            _ = b.HasMany(m => m.TagMappings)
                .WithOne(tm => tm.Model3D)
                .HasForeignKey(tm => tm.Model3DId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            _ = b.HasIndex(m => m.FileHash).IsUnique();
            _ = b.HasIndex(m => m.UploadedAt);
            _ = b.HasIndex(m => m.FileFormat);
            _ = b.HasIndex(m => m.IsValid);
            _ = b.HasIndex(m => m.UploadedByUserId);
            _ = b.HasIndex(m => m.HealthStatus); // Index for dashboard queries
            _ = b.HasIndex(m => m.LastHealthCheckDate); // Index for recent health checks
        });

        // Model3DTag Entity Configuration
        _ = modelBuilder.Entity<Model3DTag>(b =>
        {
            _ = b.HasKey(t => t.Id);
            _ = b.Property(t => t.Name).IsRequired().HasMaxLength(128);
            _ = b.Property(t => t.Color).HasMaxLength(7); // Hex color codes
            _ = b.Property(t => t.Description).HasMaxLength(512);

            // Navigation: Model3DTag -> TagMappings
            _ = b.HasMany(t => t.TagMappings)
                .WithOne(tm => tm.Tag)
                .HasForeignKey(tm => tm.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // Index for quick tag lookups
            _ = b.HasIndex(t => t.Name).IsUnique();
        });

        // Model3DTagMapping Entity Configuration
        _ = modelBuilder.Entity<Model3DTagMapping>(b =>
        {
            _ = b.HasKey(tm => tm.Id);
            _ = b.Property(tm => tm.Model3DId).IsRequired();
            _ = b.Property(tm => tm.TagId).IsRequired();

            // Foreign Keys
            _ = b.HasOne(tm => tm.Model3D)
                .WithMany(m => m.TagMappings)
                .HasForeignKey(tm => tm.Model3DId)
                .OnDelete(DeleteBehavior.Cascade);

            _ = b.HasOne(tm => tm.Tag)
                .WithMany(t => t.TagMappings)
                .HasForeignKey(tm => tm.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // Composite index to prevent duplicate tag assignments
            _ = b.HasIndex(tm => new { tm.Model3DId, tm.TagId }).IsUnique();

            // Index for finding all models with a tag
            _ = b.HasIndex(tm => tm.TagId);
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
            _ = b.Property(p => p.MetadataJson).HasColumnType("TEXT");
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

            // Indexes
            _ = b.HasIndex(p => new { p.Material, p.SlicerType }).IsUnique();
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
}
