using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Server.Data.Extensions;

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
        // Printer table optimizations
        modelBuilder.Entity<Farm.Web.Server.Domain.Printer>(entity =>
        {
            // Index for frequently queried fields
            entity.HasIndex(p => p.Backend)
                  .HasDatabaseName("IX_Printers_Backend");

            // String length constraints for better performance
            entity.Property(p => p.Name)
                  .HasMaxLength(100);
                  
            entity.Property(p => p.ServerUrl)
                  .HasMaxLength(500);
                  
            entity.Property(p => p.ApiKey)
                  .HasMaxLength(500);
                  
            entity.Property(p => p.Notes)
                  .HasMaxLength(1000);
                  
            entity.Property(p => p.OriginalServerUrl)
                  .HasMaxLength(500);
                  
            entity.Property(p => p.IpAddress)
                  .HasMaxLength(50);
        });

        // Manufacturer table optimizations
        modelBuilder.Entity<Farm.Web.Server.Domain.Manufacturer>(entity =>
        {
            entity.HasIndex(m => m.Name)
                  .IsUnique()
                  .HasDatabaseName("IX_Manufacturers_Name_Unique");
                  
            entity.Property(m => m.Name)
                  .HasMaxLength(100);
        });

        // Model table optimizations
        modelBuilder.Entity<Farm.Web.Server.Domain.PrinterModel>(entity =>
        {
            entity.HasIndex(m => m.ManufacturerId)
                  .HasDatabaseName("IX_Models_ManufacturerId");
                  
            entity.HasIndex(m => new { m.ManufacturerId, m.Name })
                  .IsUnique()
                  .HasDatabaseName("IX_Models_ManufacturerId_Name_Unique");
                  
            entity.Property(m => m.Name)
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
        switch (providerName.ToLowerInvariant())
        {
            case var provider when provider.Contains("sqlite"):
                ConfigureSqliteOptimizations(modelBuilder);
                break;
                
            case var provider when provider.Contains("postgres") || provider.Contains("npgsql"):
                ConfigurePostgreSqlOptimizations(modelBuilder);
                break;
                
            case var provider when provider.Contains("sqlserver"):
                ConfigureSqlServerOptimizations(modelBuilder);
                break;
                
            case var provider when provider.Contains("mysql"):
                ConfigureMySqlOptimizations(modelBuilder);
                break;
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
        modelBuilder.Entity<Farm.Web.Server.Domain.Printer>(entity =>
        {
            // Use included columns for covering indexes (SQL Server specific)
            entity.HasIndex(p => p.Backend)
                  .HasDatabaseName("IX_Printers_Backend_Covering");
        });
    }

    private static void ConfigureMySqlOptimizations(ModelBuilder modelBuilder)
    {
        // MySQL-specific optimizations
        // MySQL has good indexing capabilities
    }
}
