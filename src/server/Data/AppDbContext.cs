using Farm.Web.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<Spool> Spools => Set<Spool>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<PrinterModel> Models => Set<PrinterModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            b.Property(m => m.Name).IsRequired().HasMaxLength(128).UseCollation("NOCASE");
            b.HasIndex(m => m.Name).IsUnique();
        });
        modelBuilder.Entity<PrinterModel>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Name).IsRequired().HasMaxLength(128).UseCollation("NOCASE");
            b.HasOne(m => m.Manufacturer)
             .WithMany(x => x.Models)
             .HasForeignKey(m => m.ManufacturerId)
             .OnDelete(DeleteBehavior.Cascade);
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
    }
}
