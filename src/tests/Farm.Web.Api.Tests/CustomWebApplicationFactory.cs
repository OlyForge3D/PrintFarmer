using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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

namespace Farm.Web.Api.Tests
{
    // Provides isolated in-memory SQLite database for each test instance.
    // Uses SQLite in-memory with shared cache so each factory instance gets its own isolated database.
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        // Each test gets a unique in-memory database using named connection
        private readonly string _connectionString;
        private readonly string _modelStoragePath;
        private readonly string _gcodeStoragePath;
        private readonly SqliteConnection _keepAliveConnection;
        private static int _databaseCounter = 0;

        public CustomWebApplicationFactory()
        {
            // Create a unique in-memory database per factory instance
            // Using auto-increment ID ensures complete isolation between tests
            int dbId = System.Threading.Interlocked.Increment(ref _databaseCounter);
            // Use a named shared in-memory database and keep one connection open for the factory lifetime.
            // This prevents SQLite from treating the string as a file path and avoids intermittent IO errors.
            _keepAliveConnection = new SqliteConnection($"Data Source=file:farm_test_{dbId}?mode=memory&cache=shared");
            _keepAliveConnection.Open();
            _connectionString = _keepAliveConnection.ConnectionString;

            // Create temp directories for file storage (isolated per test)
            string tempDir = Path.Combine(Path.GetTempPath(), $"farm_test_{Guid.NewGuid()}");
            _modelStoragePath = Path.Combine(tempDir, "models");
            _gcodeStoragePath = Path.Combine(tempDir, "gcode");

            // Create the directories
            Directory.CreateDirectory(_modelStoragePath);
            Directory.CreateDirectory(_gcodeStoragePath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Configure worker auth shared key and storage paths for testing
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
                // Remove only the DbContext configuration, not the whole service
                ServiceDescriptor? dbContextDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextDescriptor != null)
                {
                    services.Remove(dbContextDescriptor);
                }

                // Also remove DbContextFactory and its singleton options since it was registered with the original options
                ServiceDescriptor? dbContextFactoryDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDbContextFactory<AppDbContext>));
                if (dbContextFactoryDescriptor != null)
                {
                    services.Remove(dbContextFactoryDescriptor);
                }

                // Remove singleton options that were registered for the factory
                ServiceDescriptor? singletonOptionsDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<AppDbContext>) && d.Lifetime == ServiceLifetime.Singleton);
                if (singletonOptionsDescriptor != null)
                {
                    services.Remove(singletonOptionsDescriptor);
                }

                // Register in-memory SQLite database
                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(_connectionString);
                });

                // Re-register DbContextFactory with the test SQLite connection (same pattern as production)
                DbContextOptionsBuilder<AppDbContext> optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
                optionsBuilder.UseSqlite(_connectionString);
                services.AddSingleton(optionsBuilder.Options);
                services.AddDbContextFactory<AppDbContext>();

                // Ensure database is created after all services are registered
                ServiceProvider sp = services.BuildServiceProvider();
                using (IServiceScope scope = sp.CreateScope())
                {
                    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db.Database.EnsureCreated();
                }
            });
        }

        public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
        {
            // Tests expect a factory instance configured for an isolated DB.
            return new CustomWebApplicationFactory();
        }

        /// <summary>
        /// Cleans up temporary directories created during test setup.
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            // Clean up temporary storage directories
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
                // Ignore cleanup errors (files might be locked)
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
        /// This should be used for testing endpoints that require [Authorize].
        /// </summary>
        public async Task<HttpClient> CreateAuthenticatedClientAsync(
            string username = "test-admin",
            string email = "test@example.com",
            string password = "TestPassword123!")
        {
            // Create test user
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

            // Get token from authentication service
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
        /// <summary>
        /// Resets the database by deleting all tables and recreating schema.
        /// Useful for tests that share a factory but need a fresh database state.
        /// </summary>
        public async Task ResetDatabaseAsync()
        {
            AsyncServiceScope scope = Services.CreateAsyncScope();
            try
            {
                AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await context.Database.EnsureDeletedAsync();
                await context.Database.EnsureCreatedAsync();

                // Seed root folders for gcode and models to match production behavior
                await SeedRootFoldersAsync(context);
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
                // Ensure root "/" folder exists for "gcode" category
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

                // Ensure root "/" folder exists for "models" category
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

        // Generic mock helpers: use generic type parameter so callers with
        // Action<Mock<T>> lambdas will type-infer T correctly.
        public CustomWebApplicationFactory MockNetworkDiscoveryService<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockMoonrakerClient<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockPrusaLinkClient<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSdcpClient<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSlicerJobQueue<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSlicerFileStorage<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSlicerProgressNotifier<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockModelAnalysisService<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        /// <summary>
        /// Creates an authenticated HTTP client with farm_admin role.
        /// Use for testing endpoints that require [Authorize(Policy = "farm_admin")].
        /// </summary>
        public async Task<HttpClient> CreateAdminClientAsync(
            string username = "test-admin",
            string email = "test@example.com",
            string password = "TestPassword123!")
        {
            // Create admin user with farm_admin role
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

                    // Assign farm_admin role
                    Role? adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
                    if (adminRole == null)
                    {
                        // Create the farm_admin role if it doesn't exist
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

            // Get token and create authenticated client
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
        /// Use for testing endpoints that require both [Authorize] and worker key validation.
        /// </summary>
        public async Task<HttpClient> CreateWorkerClientAsync(
            string workerKey = "test-worker-key",
            string workerName = "Test Worker",
            string username = "test-worker-user",
            string email = "worker@example.com",
            string password = "WorkerPassword123!")
        {
            // Create the worker in the database
            await RegisterWorkerAsync(workerKey, workerName);

            // Get authenticated client and add worker key header
            HttpClient client = await CreateAuthenticatedClientAsync(username, email, password);
            client.DefaultRequestHeaders.Add("X-Worker-Key", workerKey);
            return client;
        }

        /// <summary>
        /// Registers a worker in the database with the given API key.
        /// Use this for tests that need a valid worker key but don't want the header set automatically.
        /// </summary>
        public async Task RegisterWorkerAsync(
            string workerKey = "test-worker-key",
            string workerName = "Test Worker")
        {
            using (AsyncServiceScope scope = Services.CreateAsyncScope())
            {
                AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
    }
}
