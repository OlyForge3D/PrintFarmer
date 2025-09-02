using Farm.Web.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<Spool> Spools => Set<Spool>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<PrinterModel> Models => Set<PrinterModel>();
    public DbSet<SpoolmanConfig> SpoolmanConfigs => Set<SpoolmanConfig>();

    // G-code Library & Job Queue
    public DbSet<GcodeFile> GcodeFiles => Set<GcodeFile>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<PrinterCapabilities> PrinterCapabilities => Set<PrinterCapabilities>();
    public DbSet<GcodeHarvestOperation> GcodeHarvestOperations => Set<GcodeHarvestOperation>();
    public DbSet<DiscoveredGcodeFile> DiscoveredGcodeFiles => Set<DiscoveredGcodeFile>();

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
            var isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            var nameProp = b.Property(m => m.Name).IsRequired().HasMaxLength(128);
            if (isSqlite)
            {
                nameProp.UseCollation("NOCASE");
            }
            b.HasIndex(m => m.Name).IsUnique();
        });

        modelBuilder.Entity<PrinterModel>(b =>
        {
            b.HasKey(m => m.Id);
            var isSqlite = Database.ProviderName != null && Database.ProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
            var nameProp = b.Property(m => m.Name).IsRequired().HasMaxLength(128);
            if (isSqlite)
            {
                nameProp.UseCollation("NOCASE");
            }
            b.HasOne(m => m.Manufacturer)
             .WithMany(x => x.Models)
             .HasForeignKey(m => m.ManufacturerId)
             .OnDelete(DeleteBehavior.NoAction); // Changed from Cascade to NoAction to prevent multiple cascade paths
            b.HasIndex(m => new { m.ManufacturerId, m.Name }).IsUnique();
            b.Property(m => m.MaxX);
            b.Property(m => m.MaxY);
            b.Property(m => m.MaxZ);
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
    }
}
