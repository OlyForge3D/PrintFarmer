extern alias SlicerHost;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.Users;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicers.OrcaSlicer.v2_4_0;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using SlicerHostProgram = SlicerHost::Program;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Verifies model-byte security through the standalone slicer-host pipeline used by split deployments.
/// </summary>
public class StandaloneSlicerHostModelDownloadSecurityTests(
    StandaloneSlicerHostApplicationFactory factory)
    : IClassFixture<StandaloneSlicerHostApplicationFactory>
{
    [Theory]
    [InlineData("wrapped-database")]
    [InlineData("wrapped-timeout")]
    [InlineData("internal-cancellation")]
    [InlineData("invalid-operation")]
    [InlineData("missing-service")]
    [InlineData("disposed")]
    [InlineData("unexpected")]
    [InlineData("caller-cancellation")]
    public async Task TokenValidation_StoreFault_RecoversOnlyIdentifiedProviderFailures(string scenario)
    {
        using CancellationTokenSource cancellation = new();
        using HttpClient client = factory.CreateAuthenticatedClient(out Guid userId);
        string token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        Exception failure = scenario switch
        {
            "wrapped-database" => new InvalidOperationException("Provider wrapper.", new SqliteException("Store unavailable.", 14)),
            "wrapped-timeout" => new InvalidOperationException("Provider wrapper.", new TimeoutException("Store timeout.")),
            "internal-cancellation" => new OperationCanceledException("Store cancelled internally."),
            "disposed" => new ObjectDisposedException("revocation-store"),
            "caller-cancellation" => new OperationCanceledException(cancellation.Token),
            "unexpected" => new ArgumentException("Revocation implementation fault."),
            _ => new InvalidOperationException("Invalid revocation service/tracker state.")
        };
        Mock<ITokenRevocationService> revocation = new(MockBehavior.Strict);
        revocation.Setup(service => service.IsTokenRevokedAsync(token, cancellation.Token))
            .Returns(async () =>
            {
                if (scenario == "caller-cancellation")
                {
                    await cancellation.CancelAsync();
                }
                return await Task.FromException<bool>(failure);
            });
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ServiceCollection services = new();
        services.AddSingleton(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        services.AddSingleton(scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        if (scenario != "missing-service")
        {
            services.AddSingleton(revocation.Object);
        }
        using ServiceProvider provider = services.BuildServiceProvider();
        DefaultHttpContext httpContext = new()
        {
            RequestServices = provider,
            RequestAborted = cancellation.Token
        };
        Microsoft.AspNetCore.Authentication.AuthenticationScheme scheme = new(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        JwtBearerOptions options = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
        TokenValidatedContext context = new(httpContext, scheme, options)
        {
            SecurityToken = new JsonWebToken(token),
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], JwtBearerDefaults.AuthenticationScheme))
        };
        int errorsBefore = factory.AuthenticationErrors.Count();

        Exception? propagated = await Record.ExceptionAsync(() => options.Events.OnTokenValidated(context));

        if (scenario is "wrapped-database" or "wrapped-timeout" or "internal-cancellation")
        {
            Assert.Null(propagated);
            Assert.False(context.Result!.Succeeded);
            Assert.Equal("Token revocation status could not be verified.", context.Result.Failure!.Message);
            Assert.Equal(errorsBefore + 1, factory.AuthenticationErrors.Count());
        }
        else
        {
            if (scenario == "missing-service")
            {
                InvalidOperationException missingService = Assert.IsType<InvalidOperationException>(propagated);
                Assert.Contains(nameof(ITokenRevocationService), missingService.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.Same(failure, propagated);
            }
            Assert.Null(context.Result);
            Assert.Equal(errorsBefore, factory.AuthenticationErrors.Count());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_DeactivatedOrDeletedAccount_RejectsApiAndHubReconnect(bool deleteAccount)
    {
        string fileName = $"account-{Guid.NewGuid():N}.stl";
        string filePath = Path.Join(factory.ModelStoragePath, fileName);
        await File.WriteAllTextAsync(filePath, "active-account model");
        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient(out Guid userId, allowHub: true);
            string token = client.DefaultRequestHeaders.Authorization!.Parameter!;
            await using HubConnection connection = new HubConnectionBuilder()
                .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/slicers"), options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.WebSockets;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                        await factory.Server.CreateWebSocketClient().ConnectAsync(
                            new Uri(QueryHelpers.AddQueryString(context.Uri.ToString(), "access_token", token)),
                            cancellationToken);
                })
                .Build();

            using HttpResponseMessage activeResponse = await client.GetAsync(BuildViewerDownloadUrl(fileName));
            activeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            await connection.StartAsync();
            await connection.StopAsync();

            await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
            {
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.RefreshTokens.Add(new RefreshToken
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Token = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                });
                await db.SaveChangesAsync();
                UsersService users = CreateUserManagementService(scope.ServiceProvider, db);
                if (deleteAccount)
                {
                    (await users.DeleteUserAsync(userId, CancellationToken.None)).Should().BeTrue();
                    (await db.Users.AnyAsync(user => user.Id == userId)).Should().BeFalse();
                    (await db.RefreshTokens.AnyAsync(refresh => refresh.UserId == userId)).Should().BeFalse();
                }
                else
                {
                    UserDto? updated = await users.UpdateUserAsync(
                        userId, new UpdateUserRequest { IsActive = false }, userId, null, CancellationToken.None);
                    updated!.IsActive.Should().BeFalse();
                    (await db.RevokedTokens.AnyAsync(revocation => revocation.UserId == userId)).Should().BeTrue();
                    (await db.RefreshTokens.SingleAsync(refresh => refresh.UserId == userId)).IsRevoked.Should().BeTrue();
                }
            }

            using HttpResponseMessage disabledResponse = await client.GetAsync(BuildViewerDownloadUrl(fileName));
            disabledResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            HttpRequestException reconnectFailure = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
            reconnectFailure.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using HttpClient anonymousClient = factory.CreateClient();
            using HttpResponseMessage handshake = await anonymousClient.PostAsync(
                $"/hubs/slicers/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);
            handshake.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static UsersService CreateUserManagementService(IServiceProvider services, AppDbContext db)
    {
        EfUsersRepository repository = new(db);
        PasswordHashingService hasher = new();
        ITokenRevocationService revocation = services.GetRequiredService<ITokenRevocationService>();
        IAuthAuditService audit = services.GetRequiredService<IAuthAuditService>();
        AuthenticationService authentication = new(
            repository, hasher,
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<ILogger<AuthenticationService>>(),
            Mock.Of<IEmailService>(MockBehavior.Strict),
            Mock.Of<Farm.Infrastructure.Services.RateLimiting.IRateLimitService>(MockBehavior.Strict),
            Mock.Of<IAccountLockoutService>(MockBehavior.Strict),
            audit, revocation);
        return new UsersService(repository, authentication, hasher, new EffectivePermissionsRevocationService(revocation), audit);
    }

    [Theory]
    [InlineData("Users", "Unable to validate user account.")]
    [InlineData("RevokedTokens", "Token revocation status could not be verified")]
    public async Task Authenticate_StoreFailure_RejectsApiAndHubAndLogsError(string table, string expectedLog)
    {
        await using StandaloneSlicerHostApplicationFactory isolatedFactory = new();
        using HttpClient client = isolatedFactory.CreateAuthenticatedClient();
        await using (AsyncServiceScope scope = isolatedFactory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (table == "Users")
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE Users");
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync("DROP TABLE RevokedTokens");
            }
        }

        using HttpResponseMessage apiResponse = await client.GetAsync(BuildViewerDownloadUrl("missing.stl"));
        apiResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using HttpResponseMessage hubResponse = await client.PostAsync("/hubs/slicers/negotiate?negotiateVersion=1", null);
        hubResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        isolatedFactory.AuthenticationErrors.Should().Contain(message => message.Contains(expectedLog, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Authenticate_RevokedJwt_RejectsApiAndNewHubHandshake()
    {
        using HttpClient client = factory.CreateAuthenticatedClient(out Guid userId);
        string token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ITokenRevocationService revocation = scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();
            (await revocation.RevokeTokenAsync(token, userId, userId, "Test revocation")).Should().BeTrue();
        }

        using HttpResponseMessage apiResponse = await client.GetAsync(BuildViewerDownloadUrl("missing.stl"));
        apiResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using HttpResponseMessage hubResponse = await client.PostAsync("/hubs/slicers/negotiate?negotiateVersion=1", null);
        hubResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/3d-models/file/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/3d-models/download-for-viewer?path=missing.stl")]
    public async Task ModelByteEndpoint_WithoutAuthentication_Returns401(string path)
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetModelFile_WithAuthenticatedRequest_Returns200()
    {
        Guid modelId = Guid.NewGuid();
        string fileName = $"{modelId}.stl";
        string filePath = Path.Join(factory.ModelStoragePath, fileName);
        await File.WriteAllTextAsync(filePath, "split-host model file");

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            SlicerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            dbContext.Models3D.Add(new Model3D
            {
                Id = modelId,
                Name = "split-host-model.stl",
                FileName = fileName,
                FilePath = factory.ModelStoragePath,
                FileSizeBytes = new FileInfo(filePath).Length,
                FileHash = $"hash-{modelId}",
                FileFormat = ModelFileFormat.STL,
                IsValid = true,
                FolderId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await dbContext.SaveChangesAsync();

            HttpResponseMessage response = await client.GetAsync(
                $"/api/3d-models/file/{modelId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("split-host model file");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithAuthenticatedRequest_Returns200()
    {
        string fileName = $"viewer-{Guid.NewGuid():N}.stl";
        string filePath = Path.Join(factory.ModelStoragePath, fileName);
        await File.WriteAllTextAsync(filePath, "split-host viewer");

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(BuildViewerDownloadUrl(fileName));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("split-host viewer");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithTraversalPath_Returns400()
    {
        string outsidePath = CreateOutsideFile("traversal");
        string traversalPath = Path.GetRelativePath(factory.ModelStoragePath, outsidePath);

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(
                BuildViewerDownloadUrl(traversalPath));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithAbsolutePath_Returns400()
    {
        string outsidePath = CreateOutsideFile("absolute");

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(
                BuildViewerDownloadUrl(outsidePath));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DownloadForViewer_WithSymlinkOutsideStorageRoot_Returns403()
    {
        string outsidePath = CreateOutsideFile("symlink");
        string linkPath = Path.Join(
            factory.ModelStoragePath,
            $"link-{Guid.NewGuid():N}.stl");
        _ = File.CreateSymbolicLink(linkPath, outsidePath);

        try
        {
            using HttpClient client = factory.CreateAuthenticatedClient();
            HttpResponseMessage response = await client.GetAsync(
                BuildViewerDownloadUrl(Path.GetFileName(linkPath)));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(outsidePath);
        }
    }

    private string CreateOutsideFile(string prefix)
    {
        string path = Path.Join(
            factory.OutsideStoragePath,
            $"{prefix}-{Guid.NewGuid():N}.stl");
        File.WriteAllText(path, "outside");
        return path;
    }

    private static string BuildViewerDownloadUrl(string path)
    {
        return $"/api/3d-models/download-for-viewer?path={Uri.EscapeDataString(path)}";
    }
}

/// <summary>
/// Hosts the production standalone slicer entry point with deterministic test storage and JWT settings.
/// </summary>
public sealed class StandaloneSlicerHostApplicationFactory : WebApplicationFactory<SlicerHostProgram>
{
    private const string JwtKey =
        "PrintFarmerStandaloneSlicerHostModelDownloadSecurityTestsKey-1234567890";
    private const string JwtIssuer = "PrintFarmer";
    private const string JwtAudience = "PrintFarmer";

    private readonly string _testRoot;
    private readonly string _databasePath;
    private readonly AuthenticationErrorLoggerProvider _authenticationLogger = new();

    public StandaloneSlicerHostApplicationFactory()
    {
        _testRoot = Path.Join(
            Path.GetTempPath(),
            $"slicer-host-security-{Guid.NewGuid():N}");
        ModelStoragePath = Path.Join(_testRoot, "models");
        OutsideStoragePath = Path.Join(
            Path.GetTempPath(),
            $"slicer-host-security-outside-{Guid.NewGuid():N}");
        _databasePath = Path.Join(_testRoot, "slicer-host.db");
        Directory.CreateDirectory(ModelStoragePath);
        Directory.CreateDirectory(OutsideStoragePath);

        _ = typeof(OrcaSlicerLibrary_v2_4_0).Assembly.GetName();
    }

    public string ModelStoragePath { get; }

    public string OutsideStoragePath { get; }

    public IEnumerable<string> AuthenticationErrors => _authenticationLogger.Errors;

    public HttpClient CreateAuthenticatedClient() => CreateAuthenticatedClient(out _);

    public HttpClient CreateAuthenticatedClient(out Guid userId, bool allowHub = false)
    {
        userId = Guid.NewGuid();
        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "split-host-test-user"),
        ];
        if (allowHub)
        {
            claims.Add(new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Queue.Read));
        }
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = JwtIssuer,
            Audience = JwtAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                SecurityAlgorithms.HmacSha256),
        };

        HttpClient client = CreateClient();
        using (IServiceScope scope = Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"split-host-{userId:N}",
                Email = $"split-host-{userId:N}@example.com",
                PasswordHash = "unused",
                IsActive = true
            });
            db.SaveChanges();
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JsonWebTokenHandler().CreateToken(descriptor));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _ = builder.ConfigureLogging(logging => logging.AddProvider(_authenticationLogger));
        _ = builder.UseEnvironment("Testing");
        _ = builder.UseSetting("Jwt:Key", JwtKey);
        _ = builder.UseSetting("Jwt:Issuer", JwtIssuer);
        _ = builder.UseSetting("Jwt:Audience", JwtAudience);
        _ = builder.UseSetting("DB_PROVIDER", "sqlite");
        _ = builder.UseSetting(
            "ConnectionStrings:Default",
            $"Data Source={_databasePath};Pooling=False");
        _ = builder.UseSetting("STORAGE_PATHS:UPLOADS", ModelStoragePath);
        _ = builder.UseSetting(
            "STORAGE_PATHS:GCODE",
            Path.Join(_testRoot, "gcode"));
        _ = builder.UseSetting("WorkerAuth:SharedKey", "split-host-test-worker-key");
        _ = builder.UseSetting(
            "ArtifactStorage:RootPath",
            Path.Join(_testRoot, "artifacts"));
        _ = builder.UseSetting("ArtifactStorage:EnableStorageAlerts", "false");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        _ = optionsBuilder.UseSqlite($"Data Source={_databasePath};Pooling=False");
        using AppDbContext dbContext = new(optionsBuilder.Options);
        _ = dbContext.Database.EnsureCreated();

        return base.CreateHost(builder);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        if (Directory.Exists(OutsideStoragePath))
        {
            Directory.Delete(OutsideStoragePath, recursive: true);
        }
    }

    private sealed class AuthenticationErrorLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Errors { get; } = new();

        public ILogger CreateLogger(string categoryName) => new AuthenticationErrorLogger(Errors);

        public void Dispose() { }

        private sealed class AuthenticationErrorLogger(ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    errors.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
