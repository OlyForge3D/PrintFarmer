using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Web.Api.Services.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Farm.Slicer.Module.Tests
{
    /// <summary>
    /// Provides isolated in-memory SQLite database for each slicer test instance.
    /// Uses SQLite in-memory with shared cache so each factory instance gets its own isolated database.
    /// </summary>
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly string _modelStoragePath;
        private readonly string _gcodeStoragePath;
        private readonly SqliteConnection _keepAliveConnection;
        private static int _databaseCounter;

        public CustomWebApplicationFactory()
        {
            int dbId = System.Threading.Interlocked.Increment(ref _databaseCounter);
            _keepAliveConnection = new SqliteConnection($"Data Source=file:slicer_test_{dbId}?mode=memory&cache=shared");
            _keepAliveConnection.Open();
            _connectionString = _keepAliveConnection.ConnectionString;

            string tempDir = Path.Combine(Path.GetTempPath(), $"slicer_test_{Guid.NewGuid()}");
            _modelStoragePath = Path.Combine(tempDir, "models");
            _gcodeStoragePath = Path.Combine(tempDir, "gcode");

            Directory.CreateDirectory(_modelStoragePath);
            Directory.CreateDirectory(_gcodeStoragePath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkerAuth:SharedKey"] = "test-worker-key",
                    ["STORAGE_PATHS:UPLOADS"] = _modelStoragePath,
                    ["STORAGE_PATHS:GCODE"] = _gcodeStoragePath
                });
            });

            builder.ConfigureServices(services =>
            {
                // Replace SlicerDbContext with test SQLite
                ServiceDescriptor? dbContextDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<SlicerDbContext>));
                if (dbContextDescriptor != null)
                {
                    services.Remove(dbContextDescriptor);
                }

                ServiceDescriptor? dbContextFactoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDbContextFactory<SlicerDbContext>));
                if (dbContextFactoryDescriptor != null)
                {
                    services.Remove(dbContextFactoryDescriptor);
                }

                ServiceDescriptor? singletonOptionsDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<SlicerDbContext>) && d.Lifetime == ServiceLifetime.Singleton);
                if (singletonOptionsDescriptor != null)
                {
                    services.Remove(singletonOptionsDescriptor);
                }

                services.AddDbContext<SlicerDbContext>(options =>
                {
                    options.UseSqlite(_connectionString);
                });

                DbContextOptionsBuilder<SlicerDbContext> optionsBuilder = new DbContextOptionsBuilder<SlicerDbContext>();
                optionsBuilder.UseSqlite(_connectionString);
                services.AddSingleton(optionsBuilder.Options);
                services.AddDbContextFactory<SlicerDbContext>();

                ServiceProvider sp = services.BuildServiceProvider();
                using (IServiceScope scope = sp.CreateScope())
                {
                    SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                    db.Database.EnsureCreated();
                }

                // Re-configure AppDbContext to use the same test SQLite database so that
                // FolderNode and other infra entities share the same in-memory DB.
                ServiceDescriptor? appDbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (appDbDescriptor != null) { services.Remove(appDbDescriptor); }

                ServiceDescriptor? appFactoryDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<AppDbContext>));
                if (appFactoryDescriptor != null) { services.Remove(appFactoryDescriptor); }

                ServiceDescriptor? appSingletonOpts = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                    && d.Lifetime == ServiceLifetime.Singleton);
                if (appSingletonOpts != null) { services.Remove(appSingletonOpts); }

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(_connectionString);
                });

                DbContextOptionsBuilder<AppDbContext> appOptionsBuilder = new();
                appOptionsBuilder.UseSqlite(_connectionString);
                services.AddSingleton(appOptionsBuilder.Options);
                services.AddDbContextFactory<AppDbContext>();

                // Create AppDbContext tables in the shared DB
                ServiceProvider sp2 = services.BuildServiceProvider();
                using (IServiceScope scope2 = sp2.CreateScope())
                {
                    AppDbContext appDb = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
                    var appCreator = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)appDb).Instance
                        .GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
                    try { appCreator.CreateTables(); } catch (Microsoft.Data.Sqlite.SqliteException) { /* tables may already exist */ }
                }
            });
        }

        /// <summary>
        /// Creates a factory instance with an isolated in-memory database.
        /// </summary>
        public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
        {
            return new CustomWebApplicationFactory();
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                string? tempDir = Path.GetDirectoryName(_modelStoragePath);
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                _keepAliveConnection.Close();
                _keepAliveConnection.Dispose();
            }
            catch
            {
                // Ignore connection cleanup errors
            }

            await base.DisposeAsync();
        }

        /// <summary>
        /// Creates an authenticated HTTP client with a valid JWT bearer token.
        /// </summary>
        public async Task<HttpClient> CreateAuthenticatedClientAsync(
            string username = "test-admin",
            string email = "test@example.com",
            string password = "TestPassword123!")
        {
            using (AsyncServiceScope scope = Services.CreateAsyncScope())
            {
                AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

                User? existingUser = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
                if (existingUser == null)
                {
                    var user = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = username,
                        Email = email,
                        PasswordHash = passwordHasher.HashPassword(password),
                        FirstName = "Test",
                        LastName = "Admin",
                        IsActive = true,
                        EmailConfirmed = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    context.Users.Add(user);
                    await context.SaveChangesAsync();
                }
            }

            using (AsyncServiceScope scope = Services.CreateAsyncScope())
            {
                IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
                AuthenticationResult result = await authService.AuthenticateAsync(username, password);

                HttpClient client = CreateClient();
                if (result.Success && !string.IsNullOrEmpty(result.Token))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.Token}");
                }
                return client;
            }
        }

        /// <summary>
        /// Resets the database by deleting all tables and recreating schema.
        /// </summary>
        public async Task ResetDatabaseAsync()
        {
            AsyncServiceScope scope = Services.CreateAsyncScope();
            try
            {
                SlicerDbContext slicerContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                await slicerContext.Database.EnsureDeletedAsync();
                await slicerContext.Database.EnsureCreatedAsync();

                // EnsureCreated on a second context is a no-op if the DB already exists.
                // Use CreateTables() to add AppDbContext tables to the shared DB.
                AppDbContext appContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var creator = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)appContext).Instance
                    .GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
                try { creator.CreateTables(); } catch (Microsoft.Data.Sqlite.SqliteException) { /* tables may already exist */ }

                await SeedRootFoldersAsync(appContext);
            }
            finally
            {
                await scope.DisposeAsync();
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
                context.ChangeTracker.Clear();
            }
        }

        /// <summary>
        /// Creates an authenticated HTTP client with farm_admin role.
        /// </summary>
        public async Task<HttpClient> CreateAdminClientAsync(
            string username = "test-admin",
            string email = "test@example.com",
            string password = "TestPassword123!")
        {
            using (AsyncServiceScope scope = Services.CreateAsyncScope())
            {
                AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

                User? existingUser = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
                if (existingUser == null)
                {
                    var user = new User
                    {
                        Id = Guid.NewGuid(),
                        Username = username,
                        Email = email,
                        PasswordHash = passwordHasher.HashPassword(password),
                        FirstName = "Test",
                        LastName = "Admin",
                        IsActive = true,
                        EmailConfirmed = true,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    context.Users.Add(user);
                    await context.SaveChangesAsync();

                    Role? adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
                    if (adminRole == null)
                    {
                        adminRole = new Role
                        {
                            Id = Guid.NewGuid(),
                            Name = "farm_admin",
                            Description = "Farm administrator",
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        context.Roles.Add(adminRole);
                        await context.SaveChangesAsync();
                    }

                    context.UserRoles.Add(new UserRole
                    {
                        UserId = user.Id,
                        RoleId = adminRole.Id,
                        IsActive = true,
                        AssignedAt = DateTime.UtcNow
                    });
                    await context.SaveChangesAsync();
                }
            }

            using (AsyncServiceScope scope = Services.CreateAsyncScope())
            {
                IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
                AuthenticationResult result = await authService.AuthenticateAsync(username, password);

                HttpClient client = CreateClient();
                if (result.Success && !string.IsNullOrEmpty(result.Token))
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.Token}");
                }
                return client;
            }
        }

        /// <summary>
        /// Creates an authenticated HTTP client with a valid worker API key.
        /// </summary>
        public async Task<HttpClient> CreateWorkerClientAsync(
            string workerKey = "test-worker-key",
            string workerName = "Test Worker",
            string username = "test-worker-user",
            string email = "worker@example.com",
            string password = "WorkerPassword123!")
        {
            await RegisterWorkerAsync(workerKey, workerName);

            HttpClient client = await CreateAuthenticatedClientAsync(username, email, password);
            client.DefaultRequestHeaders.Add("X-Worker-Key", workerKey);
            return client;
        }

        /// <summary>
        /// Registers a worker in the database with the given API key.
        /// </summary>
        public async Task RegisterWorkerAsync(
            string workerKey = "test-worker-key",
            string workerName = "Test Worker")
        {
            using (AsyncServiceScope scope = Services.CreateAsyncScope())
            {
                SlicerDbContext context = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

                Worker? existingWorker = await context.Set<Worker>().FirstOrDefaultAsync(w => w.ApiKey == workerKey);
                if (existingWorker == null)
                {
                    var worker = new Worker
                    {
                        Id = Guid.NewGuid(),
                        ServiceId = $"worker-{Guid.NewGuid():N}",
                        Name = workerName,
                        EndpointUrl = "http://localhost:8080",
                        CapabilitiesJson = "[\"orcaslicer\"]",
                        Status = "online",
                        ApiKey = workerKey,
                        TotalSlots = 4,
                        ActiveJobs = 0,
                        LastHeartbeat = DateTime.UtcNow
                    };
                    context.Set<Worker>().Add(worker);
                    await context.SaveChangesAsync();
                }
            }
        }

        // Generic mock helpers for fluent test configuration.

        /// <summary>Configures a mock slicer job queue.</summary>
        public CustomWebApplicationFactory MockSlicerJobQueue<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        /// <summary>Configures a mock slicer file storage.</summary>
        public CustomWebApplicationFactory MockSlicerFileStorage<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        /// <summary>Configures a mock slicer progress notifier.</summary>
        public CustomWebApplicationFactory MockSlicerProgressNotifier<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        /// <summary>Configures a mock model analysis service.</summary>
        public CustomWebApplicationFactory MockModelAnalysisService<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }
    }
}
