using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

public class JwtAccountEligibilityTests
{
    private const string SigningKey = "AccountEligibilityTests-Only-NotAProductionKey12345";

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

    private static async Task<WebApplication> CreateAppAsync(bool failAccountLookup = false)
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
        if (failAccountLookup)
        {
            builder.Services.AddScoped<AppDbContext>(_ => throw new InvalidOperationException("Account store unavailable"));
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
