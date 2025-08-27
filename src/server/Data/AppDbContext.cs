using Farm.Web.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Farm.Web.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<Spool> Spools => Set<Spool>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<PrinterModel> Models => Set<PrinterModel>();
    public DbSet<SpoolmanConfig> SpoolmanConfigs => Set<SpoolmanConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite: store GUIDs as compact BLOB(16) for performance/size
        var guidToBlob = new ValueConverter<Guid, byte[]>(
            v => v.ToByteArray(),
            v => new Guid(v));
        var nullableGuidToBlob = new ValueConverter<Guid?, byte[]?>(
            v => v.HasValue ? v.Value.ToByteArray() : null,
            v => v != null ? new Guid(v) : (Guid?)null);

        modelBuilder.Entity<Printer>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasConversion(guidToBlob).HasColumnType("BLOB");
            b.Property(p => p.Name).IsRequired().HasMaxLength(128);
            b.Property(p => p.ServerUrl).IsRequired().HasMaxLength(256);
            b.Property(p => p.OriginalServerUrl).HasMaxLength(256);
            b.Property(p => p.IpAddress).HasMaxLength(64);
            b.Property(p => p.Backend).HasDefaultValue(0);
            b.Property(p => p.ApiKey);
            b.Property(p => p.ManufacturerId).HasConversion(nullableGuidToBlob).HasColumnType("BLOB");
            b.HasOne(p => p.Manufacturer)
             .WithMany()
             .HasForeignKey(p => p.ManufacturerId)
             .OnDelete(DeleteBehavior.SetNull);
            b.Property(p => p.ModelId).HasConversion(nullableGuidToBlob).HasColumnType("BLOB");
            b.HasOne(p => p.Model)
             .WithMany()
             .HasForeignKey(p => p.ModelId)
             .OnDelete(DeleteBehavior.SetNull);
            b.Property(p => p.DateAcquired);
        });
        modelBuilder.Entity<Manufacturer>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).HasConversion(guidToBlob).HasColumnType("BLOB");
            b.Property(m => m.Name).IsRequired().HasMaxLength(128).UseCollation("NOCASE");
            b.HasIndex(m => m.Name).IsUnique();
        });
        modelBuilder.Entity<PrinterModel>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).HasConversion(guidToBlob).HasColumnType("BLOB");
            b.Property(m => m.Name).IsRequired().HasMaxLength(128).UseCollation("NOCASE");
            b.HasOne(m => m.Manufacturer)
             .WithMany(x => x.Models)
             .HasForeignKey(m => m.ManufacturerId)
             .OnDelete(DeleteBehavior.Cascade);
            b.Property(m => m.ManufacturerId).HasConversion(guidToBlob).HasColumnType("BLOB");
            b.HasIndex(m => new { m.ManufacturerId, m.Name }).IsUnique();
            b.Property(m => m.MaxX);
            b.Property(m => m.MaxY);
            b.Property(m => m.MaxZ);
        });
        modelBuilder.Entity<Spool>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasConversion(guidToBlob).HasColumnType("BLOB");
            b.Property(s => s.Material).IsRequired().HasMaxLength(64);
            b.Property(s => s.ColorHex).IsRequired().HasMaxLength(16);
            b.Property(s => s.AssignedPrinterId).HasConversion(nullableGuidToBlob).HasColumnType("BLOB");
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
    }
}
