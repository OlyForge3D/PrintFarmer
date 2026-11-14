using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

public class JwtAccountEligibilityTests
{
    private const string SigningKey = "AccountEligibilityTests-Only-NotAProductionKey12345";

    [Theory]
    [InlineData("/api/protected", false)]
    [InlineData("/hubs/protected", false)]
    [InlineData("/api/protected", true)]
    [InlineData("/hubs/protected", true)]
    public async Task AuthenticateAsync_ActiveAccountWithRevocationDenied_ReturnsUnauthorized(string path, bool lookupFails)
    {
        await using WebApplication app = await CreateAppAsync(failRevocationLookup: lookupFails);
        Guid userId = Guid.NewGuid();
        string token = CreateToken(userId.ToString());
        using (IServiceScope scope = app.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = userId,
                Username = "revoked-user",
                Email = "revoked@example.com",
                PasswordHash = "unused",
                IsActive = true
            });
            await db.SaveChangesAsync();
            if (!lookupFails)
            {
                ITokenRevocationService revocation = scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();
                Assert.True(await revocation.RevokeTokenAsync(token, userId, userId, "Test revocation"));
            }
        }

        using HttpClient client = app.GetTestClient();
        if (path.StartsWith("/hubs", StringComparison.Ordinal))
        {
            path += $"?access_token={token}";
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/protected")]
    [InlineData("/hubs/protected")]
    public async Task AuthenticateAsync_AccountDeactivated_RejectsPreviouslyIssuedToken(string path)
    {
        await using WebApplication app = await CreateAppAsync();
        TestServer server = app.GetTestServer();
        Guid userId = Guid.NewGuid();
        using (IServiceScope scope = server.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User
            {
                Id = userId,
                Username = "active-user",
                Email = "active@example.com",
                PasswordHash = "unused",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = server.CreateClient();
        string token = CreateToken(userId.ToString());
        if (path.StartsWith("/hubs", StringComparison.Ordinal))
        {
            path += $"?access_token={token}";
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage activeResponse = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);

        using (IServiceScope scope = server.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User user = await db.Users.SingleAsync(candidate => candidate.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage inactiveResponse = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveResponse.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-id")]
    [InlineData("store-unavailable")]
    public async Task AuthenticateAsync_AccountCannotBeVerified_FailsClosed(string scenario)
    {
        await using WebApplication app = await CreateAppAsync(failAccountLookup: scenario == "store-unavailable");
        using HttpClient client = app.GetTestClient();
        string subject = scenario == "invalid-id" ? "not-a-guid" : Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(subject));

        using HttpResponseMessage response = await client.GetAsync("/api/protected");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("wrapped-database")]
    [InlineData("wrapped-timeout")]
    public async Task OnTokenValidated_AccountStoreFailure_FailsClosedAndLogsOriginalException(string scenario)
    {
        Exception failure = scenario switch
        {
            "wrapped-database" => new InvalidOperationException("Transient store failure", new SqliteException("Store unavailable", 14)),
            "wrapped-timeout" => new InvalidOperationException("Transient store failure", new TimeoutException("Store timeout")),
            _ => new SqliteException("Store unavailable", 14)
        };
        Mock<ILogger<Program>> logger = new();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(logger.Object)
            .AddDbContext<AppDbContext>(options => options
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(new FailingQueryInterceptor(failure)))
            .BuildServiceProvider();
        TokenValidatedContext context = CreateTokenValidatedContext(services);

        await ProgramHelpers.CreateJwtEvents(null, null).OnTokenValidated(context);

        Assert.NotNull(context.Result);
        Assert.False(context.Result.Succeeded);
        Assert.Equal("Unable to validate user account.", context.Result.Failure?.Message);
        logger.Verify(log => log.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.Is<Exception>(exception => ReferenceEquals(exception, failure)),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task OnTokenValidated_RequestCancelled_PropagatesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        using ServiceProvider services = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=:memory:"))
            .BuildServiceProvider();
        TokenValidatedContext context = CreateTokenValidatedContext(services, cancellation.Token);

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProgramHelpers.CreateJwtEvents(null, null).OnTokenValidated(context));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OnTokenValidated_RequestCancelledWithDatabaseFailure_PropagatesWithoutLogging()
    {
        using CancellationTokenSource cancellation = new();
        SqliteException failure = new("Query cancelled", 0);
        Mock<ILogger<Program>> logger = new();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(logger.Object)
            .AddDbContext<AppDbContext>(options => options
                .UseSqlite("Data Source=:memory:")
                .AddInterceptors(new FailingQueryInterceptor(failure, () => cancellation.Cancel())))
            .BuildServiceProvider();
        TokenValidatedContext context = CreateTokenValidatedContext(services, cancellation.Token);

        SqliteException propagated = await Assert.ThrowsAsync<SqliteException>(
            () => ProgramHelpers.CreateJwtEvents(null, null).OnTokenValidated(context));

        Assert.Same(failure, propagated);
        Assert.Null(context.Result);
        logger.Verify(log => log.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnTokenValidated_InvalidOrDisposedAccountStore_PropagatesOriginalException(bool disposed)
    {
        InvalidOperationException failure = disposed
            ? new ObjectDisposedException("account-store")
            : new InvalidOperationException("Invalid account-store registration");
        using ServiceProvider services = new ServiceCollection()
            .AddScoped<AppDbContext>(_ => throw failure)
            .BuildServiceProvider();
        TokenValidatedContext context = CreateTokenValidatedContext(services);

        Exception? propagated = await Record.ExceptionAsync(
            () => ProgramHelpers.CreateJwtEvents(null, null).OnTokenValidated(context));

        Assert.Same(failure, propagated);
        Assert.Null(context.Result);
    }

    private static TokenValidatedContext CreateTokenValidatedContext(
        IServiceProvider services, CancellationToken cancellationToken = default)
    {
        DefaultHttpContext httpContext = new()
        {
            RequestServices = services,
            RequestAborted = cancellationToken
        };
        AuthenticationScheme scheme = new(
            JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        return new TokenValidatedContext(httpContext, scheme, new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]))
        };
    }

    private sealed class FailingQueryInterceptor(Exception failure, Action? onExecuting = null) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            onExecuting?.Invoke();
            throw failure;
        }
    }

    private static async Task<WebApplication> CreateAppAsync(bool failAccountLookup = false, bool failRevocationLookup = false)
    {
        string databaseName = Guid.NewGuid().ToString();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = SigningKey,
            ["Jwt:Issuer"] = "PrintFarmer",
            ["Jwt:Audience"] = "PrintFarmer"
        });
        builder.Services.AddPrintFarmerAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(Mock.Of<IAuthAuditService>());
        if (failRevocationLookup)
        {
            Mock<ITokenRevocationService> revocation = new(MockBehavior.Strict);
            revocation.Setup(service => service.IsTokenRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DbUpdateException("Revocation store unavailable."));
            builder.Services.AddSingleton(revocation.Object);
        }
        else
        {
            builder.Services.AddScoped<ITokenRevocationService, TokenRevocationService>();
        }
        if (failAccountLookup)
        {
            builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        }
        else
        {
            builder.Services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        }

        WebApplication app = builder.Build();
        app.Run(async context =>
        {
            AuthenticateResult result = await context.AuthenticateAsync("Bearer");
            context.Response.StatusCode = result.Succeeded ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized;
        });
        await app.StartAsync();
        return app;
    }

    private static string CreateToken(string subject) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)]),
            Issuer = "PrintFarmer",
            Audience = "PrintFarmer",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                SecurityAlgorithms.HmacSha256)
        });
}
