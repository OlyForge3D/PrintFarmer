using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Slicer-on <see cref="HostFixture{TEntryPoint}"/> sub-fixture for
/// <c>Farm.Modules.Gcode.Tests</c> (issue #2039, epic #2019). This module's
/// <see cref="Farm.Web.Api.Controllers.GcodeFilesController"/>/<c>GcodeLibraryController</c>
/// integration coverage needs the slicer module registered (artifact-to-gcode promotion depends
/// on it), so — unlike <c>SlicerDisabledWebApplicationFactory</c> — this factory always leaves
/// <c>AddSlicerModule</c> enabled. Deliberately a narrow subset of
/// <c>Farm.Web.Api.Tests.CustomWebApplicationFactory</c>: only the isolated-SQLite host wiring
/// and <see cref="ResetDataAsync"/> that <c>GcodeLibraryServiceIntegrationTests</c> actually
/// exercises, not the admin/auth client helpers or provider mocks the larger monolith suite
/// also needs.
/// </summary>
public class CustomWebApplicationFactory : HostFixture<Program>
{
    private static int _databaseCounter;

    public CustomWebApplicationFactory()
        : base(
            $"gcode_module_test_{Interlocked.Increment(ref _databaseCounter)}",
            "Default Timeout=30;Pooling=False")
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // See Farm.Web.Api.Tests.CustomWebApplicationFactory for why EventLog/SystemLog
        // providers are stripped in every WebApplicationFactory-based test host.
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerAuth:SharedKey"] = "test-worker-key",
                ["STORAGE_PATHS:UPLOADS"] = ModelStoragePath,
                ["STORAGE_PATHS:GCODE"] = GcodeStoragePath,
            });
        });

        builder.ConfigureServices((context, services) =>
        {
            services.Configure<HostOptions>(options =>
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            foreach (ServiceDescriptor descriptor in services
                .Where(d => d.ServiceType == typeof(AppDbContext)
                    || d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                    || d.ServiceType == typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<AppDbContext>)
                    || d.ServiceType == typeof(IDbContextFactory<AppDbContext>))
                .ToList())
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite(
                    ConnectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"));
            });

            DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
            optionsBuilder.UseSqlite(
                ConnectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"));
            services.AddSingleton(optionsBuilder.Options);
            services.AddDbContextFactory<AppDbContext>();

            ServiceProvider sp = services.BuildServiceProvider();
            using (IServiceScope scope = sp.CreateScope())
            {
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            }

            // This module needs AddSlicerModule ON (artifact-to-gcode promotion depends on the
            // slicer module's repositories), so re-run its idempotent registration deterministically
            // rather than relying on host startup's own runtime assembly scan under parallel builds
            // -- see Farm.Web.Api.Tests.CustomWebApplicationFactory for the full race explanation.
            if (!services.Any(d =>
                d.ServiceType == typeof(DbContextOptions<Farm.Slicer.Module.Data.SlicerDbContext>)))
            {
                Farm.Slicer.Module.SlicerModuleExtensions.AddSlicerModule(services, context.Configuration);
            }

            ServiceDescriptor? slicerDbDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<Farm.Slicer.Module.Data.SlicerDbContext>));
            if (slicerDbDescriptor != null)
            {
                services.Remove(slicerDbDescriptor);
            }

            ServiceDescriptor? slicerFactoryDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IDbContextFactory<Farm.Slicer.Module.Data.SlicerDbContext>));
            if (slicerFactoryDescriptor != null)
            {
                services.Remove(slicerFactoryDescriptor);
            }

            services.AddDbContext<Farm.Slicer.Module.Data.SlicerDbContext>(options =>
            {
                options.UseSqlite(
                    ConnectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));
            });

            DbContextOptionsBuilder<Farm.Slicer.Module.Data.SlicerDbContext> slicerOptionsBuilder = new();
            slicerOptionsBuilder.UseSqlite(
                ConnectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));
            services.AddSingleton(slicerOptionsBuilder.Options);
            services.AddDbContextFactory<Farm.Slicer.Module.Data.SlicerDbContext>();

            ServiceProvider sp2 = services.BuildServiceProvider();
            using (IServiceScope scope2 = sp2.CreateScope())
            {
                Farm.Slicer.Module.Data.SlicerDbContext slicerDb =
                    scope2.ServiceProvider.GetRequiredService<Farm.Slicer.Module.Data.SlicerDbContext>();
                var creator = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)slicerDb).Instance
                    .GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
                try
                { creator.CreateTables(); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* tables may already exist */ }
            }
        });
    }

    // See Farm.Web.Api.Tests.CustomWebApplicationFactory.CreateHost for why concurrent host
    // builds must be serialized (BackendPluginExtensions.DiscoverAndLoadPlugins assembly scan
    // race).
    private static readonly SemaphoreSlim HostBuildLock = new(1, 1);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        HostBuildLock.Wait();
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            HostBuildLock.Release();
        }
    }

    /// <summary>
    /// Clears all row data (but keeps the schema) across both <see cref="AppDbContext"/> and
    /// <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/>, then reseeds the baseline root
    /// folders. See <c>Farm.Web.Api.Tests.CustomWebApplicationFactory.ResetDataAsync</c> for the
    /// full rationale (singleton-table exclusion, concurrency hazards).
    /// </summary>
    public async Task ResetDataAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();

        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await ClearAllTablesAsync(context.Database, excludedTables: SingletonTableNames);
        await ResetSingletonModelDataAsync(context);

        Farm.Slicer.Module.Data.SlicerDbContext? slicerContext =
            scope.ServiceProvider.GetService<Farm.Slicer.Module.Data.SlicerDbContext>();
        if (slicerContext != null)
        {
            await ClearAllTablesAsync(slicerContext.Database, excludedTables: SingletonTableNames);
        }

        IDatabaseInitializer dbInitializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
        await dbInitializer.SeedAllAsync();

        await SeedRootFoldersAsync(context);
    }

    private static readonly IReadOnlySet<string> SingletonTableNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "OutboxSequenceStates",
        "PasswordPolicies",
        "DispatchSettings",
        "CalibrationChangeFeedStates",
        "MutationCounters",
    };

    private static async Task ClearAllTablesAsync(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database,
        IReadOnlySet<string>? excludedTables = null)
    {
        System.Data.Common.DbConnection connection = database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        List<string> tables = new();
        using (System.Data.Common.DbCommand listCmd = connection.CreateCommand())
        {
            listCmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory';";
            using System.Data.Common.DbDataReader reader = await listCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string table = reader.GetString(0);
                if (excludedTables == null || !excludedTables.Contains(table))
                {
                    tables.Add(table);
                }
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        await database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            foreach (string table in tables)
            {
                // Table names are read back from sqlite_master above (our own schema), never from
                // external/user input -- raw interpolation into the DELETE statement is safe here.
#pragma warning disable EF1002
                await database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\";");
#pragma warning restore EF1002
            }

            await database.ExecuteSqlRawAsync("DELETE FROM sqlite_sequence;");
        }
        finally
        {
            await database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private async Task SeedRootFoldersAsync(AppDbContext context)
    {
        try
        {
            FolderNode? existingGcodeRoot = await context.Set<FolderNode>().AsNoTracking().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "gcode");
            if (existingGcodeRoot == null)
            {
                context.Set<FolderNode>().Add(new FolderNode
                {
                    Id = Guid.NewGuid(),
                    Path = "/",
                    FolderType = "gcode",
                    CreatedAt = DateTime.UtcNow
                });
            }

            FolderNode? existingModelsRoot = await context.Set<FolderNode>().AsNoTracking().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "models");
            if (existingModelsRoot == null)
            {
                context.Set<FolderNode>().Add(new FolderNode
                {
                    Id = Guid.NewGuid(),
                    Path = "/",
                    FolderType = "models",
                    CreatedAt = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint") == true)
        {
            // Folders already exist - this is fine, just continue
            context.ChangeTracker.Clear();
        }
    }

    private static async Task ResetSingletonModelDataAsync(AppDbContext context)
    {
        await ResetSingletonRowAsync(context, new OutboxSequenceState { Id = 1, NextSequence = 0 });
        await ResetSingletonRowAsync(context, new PasswordPolicyEntity
        {
            Id = 1,
            MinLength = 8,
            RequireUppercase = false,
            RequireLowercase = false,
            RequireDigit = false,
            RequireSymbol = false,
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await ResetSingletonRowAsync(context, new DispatchSettings());
        await ResetSingletonRowAsync(context, new CalibrationChangeFeedState { Id = 1, LastSequence = 0 });
        await ResetSingletonRowAsync(context, new MutationCounter());

        await context.SaveChangesAsync();

        // See Farm.Web.Api.Tests.CustomWebApplicationFactory.ResetSingletonModelDataAsync for why
        // IRevisionedEntity rows need this follow-up beyond the SetValues() call above.
        await ResetRevisionAsync<DispatchSettings>(context, 1);
        await ResetRevisionAsync<OutboxSequenceState>(context, 1);
    }

    private static Task ResetRevisionAsync<TEntity>(AppDbContext context, long revision)
        where TEntity : class, IRevisionedEntity
        => context.Set<TEntity>().ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Revision, revision));

    private static async Task ResetSingletonRowAsync<TEntity>(AppDbContext context, TEntity defaults)
        where TEntity : class
    {
        TEntity? existing = await context.Set<TEntity>().FirstOrDefaultAsync();
        if (existing == null)
        {
            context.Set<TEntity>().Add(defaults);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(defaults);
        }
    }
}
