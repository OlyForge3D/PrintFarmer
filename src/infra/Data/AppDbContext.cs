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
    // Legacy alias for test compatibility (deprecated)
#pragma warning disable S1133 // Legacy property kept for backward compatibility with existing tests
    [Obsolete("Use Models instead.")]
    public DbSet<PrinterModel> PrinterModels => Models;
#pragma warning restore S1133 // Legacy property kept for backward compatibility with existing tests
    public DbSet<FilamentType> FilamentTypes => Set<FilamentType>();
    public DbSet<PrinterModelFilamentType> PrinterModelFilamentTypes => Set<PrinterModelFilamentType>();
    public DbSet<SpoolmanConfig> SpoolmanConfigs => Set<SpoolmanConfig>();

    // G-code Library & Job Queue
    public DbSet<GcodeFile> GcodeFiles => Set<GcodeFile>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<PrinterCapabilities> PrinterCapabilities => Set<PrinterCapabilities>();
    public DbSet<GcodeHarvestOperation> GcodeHarvestOperations => Set<GcodeHarvestOperation>();
    public DbSet<HarvestDiscoveredFile> HarvestDiscoveredFiles => Set<HarvestDiscoveredFile>();

    // 3D Model Management & Slicer Integration
    public DbSet<Model3D> Models3D => Set<Model3D>();
    public DbSet<SlicerProfile> SlicerProfiles => Set<SlicerProfile>();
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
        modelBuilder.Entity<AppSettingsEntity>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Key).IsRequired().HasMaxLength(128);
            b.Property(a => a.SettingsJson).IsRequired().HasColumnType("TEXT");
            b.Property(a => a.UpdatedAt).IsRequired();
            b.HasIndex(a => a.Key).IsUnique();
        });

        // SystemLog Entity Configuration
        modelBuilder.Entity<SystemLog>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.CorrelationId).HasMaxLength(64);
            b.Property(l => l.Exception).HasColumnType("TEXT");
            b.Property(l => l.Level).IsRequired().HasMaxLength(32);
            b.Property(l => l.Message).IsRequired().HasMaxLength(1024);
            b.Property(l => l.Metadata).HasColumnType("TEXT");
            b.Property(l => l.Source).HasMaxLength(128);
            b.Property(l => l.Timestamp).IsRequired();
            b.HasIndex(l => l.Timestamp);
            b.HasIndex(l => l.Level);
        });
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<Printer>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired().HasMaxLength(128);
            b.Property(p => p.ServerUrl).IsRequired().HasMaxLength(256);
            b.Property(p => p.OriginalServerUrl).HasMaxLength(256);
            b.Property(p => p.IpAddress).HasMaxLength(64);
            b.Property(p => p.Backend).HasDefaultValue(0);
            b.Property(p => p.ApiKey);
            b.HasOne(p => p.Manufacturer)
             .WithMany()
             .HasForeignKey(p => p.ManufacturerId)
             .OnDelete(DeleteBehavior.Restrict); // Changed from SetNull - ManufacturerId is not nullable
            b.HasOne(p => p.Model)
             .WithMany()
             .HasForeignKey(p => p.ModelId)
             .OnDelete(DeleteBehavior.Restrict); // Changed from SetNull - ModelId is not nullable
            b.Property(p => p.DateAcquired);
        });

        modelBuilder.Entity<Manufacturer>(b =>
        {
            b.HasKey(m => m.Id);
            bool isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            PropertyBuilder<string> nameProp = b.Property(m => m.Name).IsRequired().HasMaxLength(128);
            if (isSqlite)
            {
                nameProp.UseCollation("NOCASE");
            }
            // Persisted shadow column for cross-provider case-insensitive uniqueness.
            // We populate this in SaveChanges overrides (lower-invariant) to avoid provider-specific computed syntax.
            b.Property<string>("NameLowered")
                .HasColumnName("NameLowered")
                .HasMaxLength(128)
                .IsRequired();
            b.HasIndex("NameLowered").IsUnique();
        });

        modelBuilder.Entity<PrinterModel>(b =>
        {
            b.HasKey(m => m.Id);
            bool isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            PropertyBuilder<string> nameProp = b.Property(m => m.Name).IsRequired().HasMaxLength(128);
            if (isSqlite)
            {
                nameProp.UseCollation("NOCASE");
            }
            b.HasOne(m => m.Manufacturer)
             .WithMany(x => x.Models)
             .HasForeignKey(m => m.ManufacturerId)
             .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths
            // Persisted shadow column for cross-provider case-insensitive uniqueness inside a manufacturer.
            b.Property<string>("NameLowered")
                .HasColumnName("NameLowered")
                .HasMaxLength(128)
                .IsRequired();
            b.HasIndex(nameof(PrinterModel.ManufacturerId), "NameLowered").IsUnique();

            // Basic properties
            b.Property(m => m.MotionType); // MotionType enum stored as int
            b.Property(m => m.MaxX);
            b.Property(m => m.MaxY);
            b.Property(m => m.MaxZ);
            b.Property(m => m.DefaultBackend);

            // Capability defaults
            b.Property(m => m.DefaultNozzleDiameter).HasDefaultValue(0.4);
            b.Property(m => m.HasHeatedBed).HasDefaultValue(true);
            b.Property(m => m.HasEnclosure).HasDefaultValue(false);
            b.Property(m => m.MultiMaterial).HasDefaultValue(false);
            b.Property(m => m.NumberOfExtruders).HasDefaultValue(1);
            b.Property(m => m.SupportsAutoLeveling).HasDefaultValue(false);
            b.Property(m => m.MinHotendTemp).HasDefaultValue(0);
            b.Property(m => m.MaxHotendTemp).HasDefaultValue(300);
            b.Property(m => m.MinBedTemp).HasDefaultValue(0);
            b.Property(m => m.MaxBedTemp).HasDefaultValue(120);
            b.Property(m => m.MaxPrintSpeed).HasDefaultValue(150);
        });

        modelBuilder.Entity<FilamentType>(b =>
        {
            b.HasKey(f => f.Id);
            bool isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            PropertyBuilder<string> nameProp = b.Property(f => f.Name).IsRequired().HasMaxLength(64);
            if (isSqlite)
            {
                nameProp.UseCollation("NOCASE");
            }
            b.HasIndex(f => f.Name).IsUnique();
            b.Property(f => f.DefaultHotendTemp);
            b.Property(f => f.DefaultBedTemp);
            b.Property(f => f.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<PrinterModelFilamentType>(b =>
        {
            b.HasKey(pf => new { pf.PrinterModelId, pf.FilamentTypeId });
            b.HasOne(pf => pf.PrinterModel)
             .WithMany(p => p.SupportedFilamentTypes)
             .HasForeignKey(pf => pf.PrinterModelId)
             .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(pf => pf.FilamentType)
             .WithMany(f => f.PrinterModels)
             .HasForeignKey(pf => pf.FilamentTypeId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Spool>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Material).IsRequired().HasMaxLength(64);
            b.Property(s => s.ColorHex).IsRequired().HasMaxLength(16);
            b.HasOne<Printer>()
             .WithMany()
             .HasForeignKey(s => s.AssignedPrinterId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SpoolmanConfig>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.BaseUrl).IsRequired().HasMaxLength(256);
        });

        // G-code File Entity Configuration
        modelBuilder.Entity<GcodeFile>(b =>
        {
            b.HasKey(g => g.Id);
            b.Property(g => g.OriginalFileName).IsRequired().HasMaxLength(255);
            b.Property(g => g.DisplayName).IsRequired().HasMaxLength(255);
            b.Property(g => g.FileHash).IsRequired().HasMaxLength(64);
            b.Property(g => g.FileSizeBytes).IsRequired();
            b.Property(g => g.FilePath).IsRequired().HasMaxLength(512);
            b.Property(g => g.SlicerName).HasMaxLength(128);
            b.Property(g => g.SlicerVersion).HasMaxLength(64);
            b.Property(g => g.RequiredMaterial).HasMaxLength(64);
            b.Property(g => g.SlicerSettings).HasColumnType("TEXT");
            b.Property(g => g.HealthStatus).HasConversion<int>().HasDefaultValue(0); // Unknown
            b.Property(g => g.LastVerificationResult).HasColumnType("TEXT");

            // JSON array properties
            b.Property(g => g.CompatibleMaterials)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null));
            b.Property(g => g.PrintTemperatures)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<double[]>(v, (System.Text.Json.JsonSerializerOptions?)null));
            b.Property(g => g.TargetPrinterModels)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null));

            // Foreign Keys - Use NoAction to avoid cascade conflicts in SQL Server
            b.HasOne(g => g.SourcePrinter)
                .WithMany()
                .HasForeignKey(g => g.SourcePrinterId)
                .OnDelete(DeleteBehavior.NoAction);
            b.HasOne(g => g.TargetPrinter)
                .WithMany()
                .HasForeignKey(g => g.TargetPrinterId)
                .OnDelete(DeleteBehavior.NoAction);
            b.HasOne(g => g.TargetModel)
                .WithMany()
                .HasForeignKey(g => g.TargetModelId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes
            b.HasIndex(g => g.FileHash).IsUnique();
            b.HasIndex(g => g.UploadedAt);
            b.HasIndex(g => g.RequiredNozzleDiameter);
            b.HasIndex(g => g.RequiredMaterial);
            b.HasIndex(g => g.TargetPrinterId);
            b.HasIndex(g => g.SourcePrinterId);
            b.HasIndex(g => g.HealthStatus); // Index for dashboard queries
            b.HasIndex(g => g.LastHealthCheckDate); // Index for recent health checks
        });

        // Print Job Entity Configuration
        modelBuilder.Entity<PrintJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.Name).IsRequired().HasMaxLength(255);
            b.Property(j => j.Status).HasConversion<int>();
            b.Property(j => j.Priority).HasDefaultValue(0);
            b.Property(j => j.EstimatedPrintTime).HasConversion<long>();
            b.Property(j => j.ActualPrintTime).HasConversion<long>();

            // JSON array properties
            b.Property(j => j.RequiredCapabilities)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null));
            b.Property(j => j.PreferredPrinterIds)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<Guid[]>(v, (System.Text.Json.JsonSerializerOptions?)null));
            b.Property(j => j.ExcludedPrinterIds)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<Guid[]>(v, (System.Text.Json.JsonSerializerOptions?)null));

            // Foreign Keys - Use NoAction to avoid cascade conflicts
            b.HasOne(j => j.GcodeFile)
                .WithMany()
                .HasForeignKey(j => j.GcodeFileId)
                .OnDelete(DeleteBehavior.NoAction);
            b.HasOne(j => j.AssignedPrinter)
                .WithMany()
                .HasForeignKey(j => j.AssignedPrinterId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes
            b.HasIndex(j => j.Status);
            b.HasIndex(j => j.QueuedAt);
            b.HasIndex(j => j.Priority);
            b.HasIndex(j => j.AssignedPrinterId);
        });

        // Printer Capabilities Entity Configuration
        modelBuilder.Entity<PrinterCapabilities>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.SupportedMaterials)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null));

            // Foreign Key - One-to-one relationship
            b.HasOne(c => c.Printer)
                .WithOne(p => p.Capabilities)
                .HasForeignKey<PrinterCapabilities>(c => c.PrinterId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            b.HasIndex(c => c.PrinterId).IsUnique();
            b.HasIndex(c => c.NozzleDiameter);
            b.HasIndex(c => c.IsAvailable);
        });

        // G-code Harvest Operation Entity Configuration
        modelBuilder.Entity<GcodeHarvestOperation>(b =>
        {
            b.HasKey(h => h.Id);
            b.Property(h => h.Status).HasConversion<int>();

            // Foreign Key
            b.HasOne(h => h.Printer)
                .WithMany()
                .HasForeignKey(h => h.PrinterId)
                .OnDelete(DeleteBehavior.NoAction);

            // Indexes
            b.HasIndex(h => h.PrinterId);
            b.HasIndex(h => h.StartedAt);
            b.HasIndex(h => h.Status);
        });

        // HarvestDiscoveredFile Entity Configuration
        modelBuilder.Entity<HarvestDiscoveredFile>(b =>
        {
            b.HasKey(f => f.Id);
            b.Property(f => f.HarvestOperationId).IsRequired();
            b.Property(f => f.FilePath).IsRequired().HasMaxLength(512);
            b.Property(f => f.FileName).IsRequired().HasMaxLength(256);
            b.Property(f => f.Size).IsRequired();
            b.Property(f => f.ThumbnailUrl).HasMaxLength(512);
            b.Property(f => f.Status).IsRequired();
            b.Property(f => f.Error).HasMaxLength(512);
            b.Property(f => f.DiscoveredAt).IsRequired();
            b.Property(f => f.StartedAt);
            b.Property(f => f.CompletedAt);
            b.HasIndex(f => f.HarvestOperationId);
        });

        // User Entity Configuration
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Username).IsRequired().HasMaxLength(50);
            b.Property(u => u.Email).IsRequired().HasMaxLength(255);
            b.Property(u => u.PasswordHash).IsRequired().HasMaxLength(255);
            b.Property(u => u.FirstName).HasMaxLength(100);
            b.Property(u => u.LastName).HasMaxLength(100);
            b.Property(u => u.EmailConfirmationToken).HasMaxLength(255);
            b.Property(u => u.PasswordResetToken).HasMaxLength(255);

            // Unique constraints
            b.HasIndex(u => u.Username).IsUnique();
            b.HasIndex(u => u.Email).IsUnique();
            b.HasIndex(u => u.IsActive);
            b.HasIndex(u => u.CreatedAt);
        });

        // Role Entity Configuration
        modelBuilder.Entity<Role>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).IsRequired().HasMaxLength(50);
            b.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
            b.Property(r => r.Description).HasColumnType("TEXT");

            // Unique constraints
            b.HasIndex(r => r.Name).IsUnique();
            b.HasIndex(r => r.IsSystemRole);
            b.HasIndex(r => r.IsActive);
        });

        // Resource Entity Configuration
        modelBuilder.Entity<Resource>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).IsRequired().HasMaxLength(100);
            b.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
            b.Property(r => r.Description).HasColumnType("TEXT");
            b.Property(r => r.ResourceType).IsRequired().HasMaxLength(50);

            // Unique constraints
            b.HasIndex(r => r.Name).IsUnique();
            b.HasIndex(r => r.ResourceType);
            b.HasIndex(r => r.IsActive);
        });

        // Action Entity Configuration
        modelBuilder.Entity<Domain.Action>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Name).IsRequired().HasMaxLength(50);
            b.Property(a => a.DisplayName).IsRequired().HasMaxLength(100);
            b.Property(a => a.Description).HasColumnType("TEXT");

            // Unique constraints
            b.HasIndex(a => a.Name).IsUnique();
        });

        // RolePermission Entity Configuration
        modelBuilder.Entity<RolePermission>(b =>
        {
            b.HasKey(rp => rp.Id);

            // Foreign Keys
            b.HasOne(rp => rp.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(rp => rp.Resource)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.ResourceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(rp => rp.Action)
                .WithMany(a => a.RolePermissions)
                .HasForeignKey(rp => rp.ActionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint - one permission per role-resource-action combination
            b.HasIndex(rp => new { rp.RoleId, rp.ResourceId, rp.ActionId }).IsUnique();
        });

        // UserRole Entity Configuration
        modelBuilder.Entity<UserRole>(b =>
        {
            b.HasKey(ur => ur.Id);

            // Foreign Keys
            b.HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint - one assignment per user-role combination
            b.HasIndex(ur => new { ur.UserId, ur.RoleId }).IsUnique();
            b.HasIndex(ur => ur.IsActive);
            b.HasIndex(ur => ur.ExpiresAt);
        });

        // RefreshToken Entity Configuration
        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasKey(rt => rt.Id);
            b.Property(rt => rt.Token).IsRequired().HasMaxLength(512);
            b.Property(rt => rt.CreatedByIp).IsRequired().HasMaxLength(45);
            b.Property(rt => rt.RevokedByIp).HasMaxLength(45);
            b.Property(rt => rt.ReplacedByToken).HasMaxLength(512);

            // Foreign Key
            b.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            b.HasIndex(rt => rt.Token).IsUnique();
            b.HasIndex(rt => rt.UserId);
            b.HasIndex(rt => rt.ExpiresAt);
            b.HasIndex(rt => rt.IsRevoked);
        });

        modelBuilder.Entity<PasswordResetToken>(b =>
        {
            b.HasKey(prt => prt.Id);
            b.Property(prt => prt.Token).IsRequired().HasMaxLength(256);
            b.Property(prt => prt.UsedByIp).HasMaxLength(45);

            // Foreign Key
            b.HasOne(prt => prt.User)
                .WithMany()
                .HasForeignKey(prt => prt.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            b.HasIndex(prt => prt.Token).IsUnique();
            b.HasIndex(prt => prt.UserId);
            b.HasIndex(prt => prt.ExpiresAt);
            b.HasIndex(prt => prt.IsUsed);
        });

        modelBuilder.Entity<AuthAuditLog>(b =>
        {
            b.HasKey(aal => aal.Id);
            b.Property(aal => aal.EventType).IsRequired();
            b.Property(aal => aal.Timestamp).IsRequired();
            b.Property(aal => aal.IpAddress).HasMaxLength(45);
            b.Property(aal => aal.UserAgent).HasMaxLength(512);
            b.Property(aal => aal.FailureReason).HasMaxLength(512);
            b.Property(aal => aal.Metadata).HasColumnType("TEXT");
            b.Property(aal => aal.CorrelationId).HasMaxLength(64);

            // Foreign Key (nullable - for failed logins where user doesn't exist)
            b.HasOne(aal => aal.User)
                .WithMany()
                .HasForeignKey(aal => aal.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes for common queries
            b.HasIndex(aal => aal.UserId);
            b.HasIndex(aal => aal.EventType);
            b.HasIndex(aal => aal.Timestamp);
            b.HasIndex(aal => aal.Success);
            b.HasIndex(aal => new { aal.UserId, aal.Timestamp }); // Common query pattern
        });

        modelBuilder.Entity<RevokedToken>(b =>
        {
            b.HasKey(rt => rt.Id);
            b.Property(rt => rt.TokenHash).IsRequired().HasMaxLength(64); // SHA256 hash = 64 hex chars
            b.Property(rt => rt.Reason).IsRequired().HasMaxLength(512);
            b.Property(rt => rt.IpAddress).HasMaxLength(45);

            // Foreign Keys
            b.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths in SQL Server

            b.HasOne(rt => rt.RevokedByUser)
                .WithMany()
                .HasForeignKey(rt => rt.RevokedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes for fast token lookup and cleanup
            b.HasIndex(rt => rt.TokenHash).IsUnique(); // Fast revocation check
            b.HasIndex(rt => rt.UserId); // Get all revoked tokens for a user
            b.HasIndex(rt => rt.ExpiresAt); // Cleanup expired revocations
            b.HasIndex(rt => rt.RevokedAt); // Audit queries
        });

        // Model3D Entity Configuration
        modelBuilder.Entity<Model3D>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.OriginalFileName).IsRequired().HasMaxLength(255);
            b.Property(m => m.DisplayName).IsRequired().HasMaxLength(255);
            b.Property(m => m.FilePath).IsRequired().HasMaxLength(512);
            b.Property(m => m.FileHash).IsRequired().HasMaxLength(64);
            b.Property(m => m.FileFormat).HasConversion<int>();
            b.Property(m => m.FileSizeBytes).IsRequired();
            b.Property(m => m.Tags).HasColumnType("TEXT");
            b.Property(m => m.ValidationErrors).HasColumnType("TEXT");
            b.Property(m => m.HealthStatus).HasConversion<int>().HasDefaultValue(0); // Unknown
            b.Property(m => m.LastVerificationResult).HasColumnType("TEXT");

            // Foreign Key
            b.HasOne(m => m.UploadedByUser)
                .WithMany()
                .HasForeignKey(m => m.UploadedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            b.HasIndex(m => m.FileHash).IsUnique();
            b.HasIndex(m => m.UploadedAt);
            b.HasIndex(m => m.FileFormat);
            b.HasIndex(m => m.IsValid);
            b.HasIndex(m => m.UploadedByUserId);
            b.HasIndex(m => m.HealthStatus); // Index for dashboard queries
            b.HasIndex(m => m.LastHealthCheckDate); // Index for recent health checks
        });

        // SlicerProfile Entity Configuration
        modelBuilder.Entity<SlicerProfile>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired().HasMaxLength(255);
            b.Property(p => p.Description).HasMaxLength(1000);
            b.Property(p => p.SlicerType).HasConversion<int>();
            b.Property(p => p.Quality).HasConversion<int>();
            b.Property(p => p.Material).IsRequired().HasMaxLength(64);
            b.Property(p => p.AdvancedSettings).HasColumnType("TEXT");
            b.Property(p => p.RawJson).HasColumnType("TEXT");
            b.Property(p => p.MetadataJson).HasColumnType("TEXT");
            b.Property(p => p.Hash).HasMaxLength(64);
            b.Property(p => p.IsSystem).HasDefaultValue(false);

            // Foreign Keys
            b.HasOne(p => p.PrinterModel)
                .WithMany()
                .HasForeignKey(p => p.PrinterModelId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(p => p.SpecificPrinter)
                .WithMany()
                .HasForeignKey(p => p.SpecificPrinterId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(p => p.CreatedByUser)
                .WithMany()
                .HasForeignKey(p => p.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            // Indexes
            b.HasIndex(p => new { p.Name, p.SlicerType, p.PrinterModelId }).IsUnique();
            b.HasIndex(p => p.SlicerType);
            b.HasIndex(p => p.PrinterModelId);
            b.HasIndex(p => p.IsDefault);
            b.HasIndex(p => p.IsPublic);
            b.HasIndex(p => p.CreatedByUserId);
            b.HasIndex(p => p.Hash).IsUnique();
            b.HasIndex(p => p.IsSystem);
        });

        // Slicer Service (Registry) Entity Configuration
        modelBuilder.Entity<SlicerService>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).IsRequired().HasMaxLength(200);
            b.Property(s => s.Version).HasMaxLength(64);
            b.Property(s => s.Host).HasMaxLength(512);
            b.Property(s => s.UiManifestUrl).HasMaxLength(512);
            b.Property(s => s.CapabilitiesJson).HasColumnType("TEXT");
            b.Property(s => s.Status).HasMaxLength(64);
            b.Property(s => s.ApiKey).HasMaxLength(128);
            b.HasIndex(s => s.Name);
            b.HasIndex(s => s.SlicerType);
            b.HasIndex(s => s.Status);
        });

        // SliceJob Entity Configuration
        modelBuilder.Entity<SliceJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.UserId).IsRequired();
            b.Property(j => j.ModelFileUrl).IsRequired().HasMaxLength(2048);
            b.Property(j => j.ModelFileName).IsRequired().HasMaxLength(512);
            b.Property(j => j.SlicerEngine).IsRequired();
            b.Property(j => j.SlicerProfileJson).HasColumnType("TEXT");
            b.Property(j => j.SlicerProfileId);
            b.Property(j => j.RequiredCapabilitiesJson).HasColumnType("TEXT");
            b.Property(j => j.Status).IsRequired().HasMaxLength(50);
            b.Property(j => j.Priority).IsRequired();
            b.Property(j => j.QueuedAt).IsRequired();
            b.Property(j => j.ResultFileUrl).HasMaxLength(2048);
            b.Property(j => j.ErrorMessage).HasColumnType("TEXT");
            b.Property(j => j.ProgressMessage).HasMaxLength(512);
            b.Property(j => j.CreatedAt).IsRequired();
            b.Property(j => j.UpdatedAt).IsRequired();

            // Indexes for efficient querying
            b.HasIndex(j => j.UserId);
            b.HasIndex(j => j.PrinterId);
            b.HasIndex(j => j.Status);
            b.HasIndex(j => j.QueuedAt);
            b.HasIndex(j => new { j.Status, j.Priority, j.QueuedAt }); // For queue processing
            b.HasIndex(j => j.WorkerId);
            b.HasIndex(j => j.SlicerProfileId);

            // Foreign key to SlicerProfile (optional reference). If profile deleted later we retain immutable snapshot JSON.
            b.HasOne(j => j.SlicerProfile)
                .WithMany()
                .HasForeignKey(j => j.SlicerProfileId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Artifact Entity Configuration
        modelBuilder.Entity<Artifact>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.JobId).IsRequired();
            b.Property(a => a.Kind).IsRequired().HasMaxLength(64);
            b.Property(a => a.FileName).IsRequired().HasMaxLength(256);
            b.Property(a => a.RelativePath).IsRequired().HasMaxLength(1024);
            b.Property(a => a.ContentType).IsRequired().HasMaxLength(128);
            b.Property(a => a.SizeBytes).IsRequired();
            b.Property(a => a.Sha256).IsRequired().HasMaxLength(64);
            b.Property(a => a.CreatedAt).IsRequired();

            // Helpful indexes for lookup & listing
            b.HasIndex(a => a.JobId);
            b.HasIndex(a => a.WorkerId);
            b.HasIndex(a => a.CreatedAt);
            b.HasIndex(a => new { a.JobId, a.Kind });
        });

        // Worker Entity Configuration
        modelBuilder.Entity<Worker>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.ServiceId).IsRequired().HasMaxLength(256);
            b.Property(w => w.Name).IsRequired().HasMaxLength(256);
            b.Property(w => w.EndpointUrl).IsRequired().HasMaxLength(2048);
            b.Property(w => w.CapabilitiesJson).HasColumnType("TEXT");
            b.Property(w => w.Status).IsRequired().HasMaxLength(50);
            b.Property(w => w.FreeSlots).IsRequired();
            b.Property(w => w.TotalSlots).IsRequired();
            b.Property(w => w.RegisteredAt).IsRequired();
            b.Property(w => w.ApiKey).HasMaxLength(512);
            b.Property(w => w.Version).HasMaxLength(50);
            b.Property(w => w.MetadataJson).HasColumnType("TEXT");
            b.Property(w => w.CreatedAt).IsRequired();
            b.Property(w => w.UpdatedAt).IsRequired();
            b.Property(w => w.DisabledReason).HasMaxLength(1024);

            // Indexes for efficient querying
            b.HasIndex(w => w.ServiceId).IsUnique();
            b.HasIndex(w => w.Status);
            b.HasIndex(w => w.LastHeartbeat);
            b.HasIndex(w => new { w.Status, w.FreeSlots }); // For worker selection
        });

        modelBuilder.Entity<PasswordPolicyEntity>(b =>
        {
            // Keep the existing table name to avoid creating a migration due to the rename
            b.ToTable("PasswordPolicies");
            b.HasKey(pp => pp.Id);
            b.Property(pp => pp.MinLength).IsRequired();
            b.Property(pp => pp.RequireUppercase);
            b.Property(pp => pp.RequireLowercase);
            b.Property(pp => pp.RequireDigit);
            b.Property(pp => pp.RequireSymbol);
        });

        // SlicerSettings Entity Configuration
        modelBuilder.Entity<SlicerSettings>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Enabled).IsRequired();
            b.Property(s => s.PerEngineJson).HasColumnType("TEXT");
            b.Property(s => s.UpdatedAt).IsRequired();
            b.Property(s => s.JitterPercent).HasDefaultValue(15.0).IsRequired();
        });

        // HarvestDiscoveredFile Entity Configuration
        modelBuilder.Entity<HarvestDiscoveredFile>(b =>
        {
            b.HasKey(f => f.Id);
            b.Property(f => f.HarvestOperationId).IsRequired();
            b.Property(f => f.FilePath).IsRequired().HasMaxLength(512);
            b.Property(f => f.FileName).IsRequired().HasMaxLength(256);
            b.Property(f => f.Size).IsRequired();
            b.Property(f => f.ThumbnailUrl).HasMaxLength(512);
            b.Property(f => f.Status).IsRequired();
            b.Property(f => f.Error).HasMaxLength(512);
            b.Property(f => f.DiscoveredAt).IsRequired();
            b.Property(f => f.StartedAt);
            b.Property(f => f.CompletedAt);
            b.HasIndex(f => f.HarvestOperationId);
        });

        // File Health Audit Entity Configuration
        modelBuilder.Entity<FileHealthAudit>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.AuditDate).IsRequired();
            b.Property(a => a.AuditType).HasConversion<int>();
            b.Property(a => a.FilesChecked).IsRequired();
            b.Property(a => a.HealthyFiles).IsRequired();
            b.Property(a => a.MissingFiles).IsRequired();
            b.Property(a => a.CorruptedFiles).IsRequired();
            b.Property(a => a.OrphanedFiles).IsRequired();
            b.Property(a => a.MissingFileIds).HasColumnType("TEXT"); // JSON array
            b.Property(a => a.CorruptedFileIds).HasColumnType("TEXT"); // JSON array
            b.Property(a => a.OrphanedFilePaths).HasColumnType("TEXT"); // JSON array
            b.Property(a => a.SummaryMessage).HasColumnType("TEXT");
            b.Property(a => a.HasIssues).IsRequired();
            b.Property(a => a.CreatedAt).IsRequired();

            // Indexes for efficient querying and dashboard
            b.HasIndex(a => a.AuditDate).IsDescending(); // Most recent audits first
            b.HasIndex(a => a.AuditType);
            b.HasIndex(a => a.HasIssues);
            b.HasIndex(a => new { a.AuditType, a.AuditDate }).IsDescending(false, true); // Composite for type+recent queries
        });

        // Seed default password policy if table empty (idempotent for EnsureCreated)
        if (Database.ProviderName != null)
        {
            // Use a static value for UpdatedAt to avoid model instability in migrations
            modelBuilder.Entity<PasswordPolicyEntity>().HasData(new PasswordPolicyEntity
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
