using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Data.Extensions;

/// <summary>
/// Extension methods for configuring database performance optimizations
/// </summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Configures indexes and constraints for optimal query performance
    /// </summary>
    public static void ConfigurePerformanceOptimizations(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        // Printer table optimizations
        _ = modelBuilder.Entity<Printer>(entity =>
        {
            // Index for frequently queried fields
            _ = entity.HasIndex(p => p.Backend)
                  .HasDatabaseName("IX_Printers_Backend");

            // String length constraints for better performance
            _ = entity.Property(p => p.Name)
                  .HasMaxLength(100);

            _ = entity.Property(p => p.ServerUrl)
                  .HasMaxLength(500);

            _ = entity.Property(p => p.ApiKey)
                  .HasMaxLength(500);

            _ = entity.Property(p => p.Notes)
                  .HasMaxLength(1000);

            _ = entity.Property(p => p.OriginalServerUrl)
                  .HasMaxLength(500);

            _ = entity.Property(p => p.IpAddress)
                  .HasMaxLength(50);
        });

        // Manufacturer table optimizations
        _ = modelBuilder.Entity<Manufacturer>(entity =>
        {
            _ = entity.HasIndex(m => m.Name)
                  .IsUnique()
                  .HasDatabaseName("IX_Manufacturers_Name_Unique");

            _ = entity.Property(m => m.Name)
                  .HasMaxLength(100);
        });

        // Model table optimizations
        _ = modelBuilder.Entity<PrinterModel>(entity =>
        {
            _ = entity.HasIndex(m => m.ManufacturerId)
                  .HasDatabaseName("IX_Models_ManufacturerId");

            _ = entity.HasIndex(m => new { m.ManufacturerId, m.Name })
                  .IsUnique()
                  .HasDatabaseName("IX_Models_ManufacturerId_Name_Unique");

            _ = entity.Property(m => m.Name)
                  .HasMaxLength(100);
        });

        // Add computed columns for common calculations - Remove since CreatedAt doesn't exist
        // Note: Syntax varies by database provider
    }

    /// <summary>
    /// Configures database-specific optimizations
    /// </summary>
    public static void ConfigureDatabaseSpecificOptimizations(this ModelBuilder modelBuilder, string providerName)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        // Perform provider detection using case-insensitive comparisons to avoid culture-sensitive casing
        string provider = providerName;
        if (provider.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureSqliteOptimizations(modelBuilder);
        }
        else if (provider.Contains("postgres", StringComparison.OrdinalIgnoreCase) || provider.Contains("npgsql", StringComparison.OrdinalIgnoreCase))
        {
            ConfigurePostgreSqlOptimizations(modelBuilder);
        }
        else if (provider.Contains("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureSqlServerOptimizations(modelBuilder);
        }
        else if (provider.Contains("mysql", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureMySqlOptimizations(modelBuilder);
        }
    }

    private static void ConfigureSqliteOptimizations(ModelBuilder modelBuilder)
    {
        // SQLite-specific optimizations
        // SQLite has limited computed column support, so keep it simple
    }

    private static void ConfigurePostgreSqlOptimizations(ModelBuilder modelBuilder)
    {
        // PostgreSQL-specific optimizations
        // PostgreSQL has excellent full-text search capabilities
    }

    private static void ConfigureSqlServerOptimizations(ModelBuilder modelBuilder)
    {
        // SQL Server-specific optimizations
        _ = modelBuilder.Entity<Printer>(entity =>
        {
            // Use included columns for covering indexes (SQL Server specific)
            _ = entity.HasIndex(p => p.Backend)
                .HasDatabaseName("IX_Printers_Backend_Covering");
        });
    }

    private static void ConfigureMySqlOptimizations(ModelBuilder modelBuilder)
    {
        // MySQL-specific optimizations
        // MySQL has good indexing capabilities
    }
}
