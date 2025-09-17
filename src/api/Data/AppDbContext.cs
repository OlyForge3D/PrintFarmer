using Farm.Web.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Web.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
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
    public DbSet<PrinterCapabilities> PrinterCapabilities => Set<PrinterCapabilities>();
    public DbSet<GcodeHarvestOperation> GcodeHarvestOperations => Set<GcodeHarvestOperation>();
    public DbSet<DiscoveredGcodeFile> DiscoveredGcodeFiles => Set<DiscoveredGcodeFile>();

    // 3D Model Management & Slicer Integration
    public DbSet<Model3D> Models3D => Set<Model3D>();
    public DbSet<SlicerProfile> SlicerProfiles => Set<SlicerProfile>();
    public DbSet<SlicerSettings> SlicerSettings => Set<SlicerSettings>();

    // User Management & Authentication
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Domain.Action> Actions => Set<Domain.Action>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<PasswordPolicy> PasswordPolicies => Set<PasswordPolicy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
             .OnDelete(DeleteBehavior.SetNull);
            b.HasOne(p => p.Model)
             .WithMany()
             .HasForeignKey(p => p.ModelId)
             .OnDelete(DeleteBehavior.SetNull);
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
            b.Property(m => m.Type); // PrinterType enum stored as int
            b.Property(m => m.MaxX);
            b.Property(m => m.MaxY);
            b.Property(m => m.MaxZ);
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

        // Discovered G-code File Entity Configuration
        modelBuilder.Entity<DiscoveredGcodeFile>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.PrinterPath).IsRequired().HasMaxLength(512);
            b.Property(d => d.FileName).IsRequired().HasMaxLength(255);
            b.Property(d => d.FileHash).HasMaxLength(64);
            b.Property(d => d.ExtractedSlicerName).HasMaxLength(128);
            b.Property(d => d.ExtractedSlicerVersion).HasMaxLength(64);
            b.Property(d => d.ExtractedMaterial).HasMaxLength(64);
            b.Property(d => d.ExtractedLayerHeight).HasMaxLength(32);
            b.Property(d => d.ExtractedInfill).HasMaxLength(32);

            // Foreign Key
            b.HasOne(d => d.HarvestOperation)
                .WithMany()
                .HasForeignKey(d => d.HarvestOperationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indexes
            b.HasIndex(d => d.HarvestOperationId);
            b.HasIndex(d => d.FileHash);
            b.HasIndex(d => d.IsSelected);
            b.HasIndex(d => d.AlreadyInLibrary);
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
        });

        modelBuilder.Entity<PasswordPolicy>(b =>
        {
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

        // Seed default password policy if table empty (idempotent for EnsureCreated)
        if (Database.ProviderName != null)
        {
            modelBuilder.Entity<PasswordPolicy>().HasData(new PasswordPolicy
            {
                Id = 1,
                MinLength = 8,
                RequireUppercase = false,
                RequireLowercase = false,
                RequireDigit = false,
                RequireSymbol = false,
                UpdatedAt = DateTime.UtcNow
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
