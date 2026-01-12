using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Web.Api.Services.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
        private static int _databaseCounter = 0;

        public CustomWebApplicationFactory()
        {
            // Create a unique in-memory database per factory instance
            // Using auto-increment ID ensures complete isolation between tests
            var dbId = System.Threading.Interlocked.Increment(ref _databaseCounter);
            _connectionString = $"Data Source=:memory:?mode=memory&cache=shared";

            // Create temp directories for file storage (isolated per test)
            var tempDir = Path.Combine(Path.GetTempPath(), $"farm_test_{Guid.NewGuid()}");
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
                var dbContextDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextDescriptor != null)
                {
                    services.Remove(dbContextDescriptor);
                }

                // Register in-memory SQLite database
                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(_connectionString);
                });

                // Ensure database is created after all services are registered
                var sp = services.BuildServiceProvider();
                using (var scope = sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
                var tempDir = Path.GetDirectoryName(_modelStoragePath);
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors (files might be locked)
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
            using (var scope = Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

                var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
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
            using (var scope = Services.CreateAsyncScope())
            {
                var authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
                var result = await authService.AuthenticateAsync(username, password);

                var client = CreateClient();
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
            var scope = Services.CreateAsyncScope();
            try
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
                var existingGcodeRoot = await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "gcode");
                if (existingGcodeRoot == null)
                {
                    context.Folders.Add(new FolderNode
                    {
                        Id = Guid.NewGuid(),
                        Path = "/",
                        FolderType = "gcode",
                        CreatedAt = DateTime.UtcNow
                    });
                }

                // Ensure root "/" folder exists for "models" category
                var existingModelsRoot = await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "models");
                if (existingModelsRoot == null)
                {
                    context.Folders.Add(new FolderNode
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
            using (var scope = Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

                var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
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
                    var adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
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
            using (var scope = Services.CreateAsyncScope())
            {
                var authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
                var result = await authService.AuthenticateAsync(username, password);

                var client = CreateClient();
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
            var client = await CreateAuthenticatedClientAsync(username, email, password);
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
            using (var scope = Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var existingWorker = await context.Workers.FirstOrDefaultAsync(w => w.ApiKey == workerKey);
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
                    context.Workers.Add(worker);
                    await context.SaveChangesAsync();
                }
            }
        }
    }
}
