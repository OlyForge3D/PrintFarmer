using System.Net;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Network;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.IntegrationTests;

[Collection("Sequential")]
public sealed class CustomWebApplicationFactoryTests
{
    private static readonly string[] FactoryEnvironmentVariables =
    [
        "ASPNETCORE_ENVIRONMENT",
        "ConnectionStrings__Default",
        "ConnectionStrings__Sqlite",
        "ConnectionStrings__DefaultConnection",
        "ConnectionStrings__SlicerDatabase",
        "TEST_USE_SQLITE_INMEMORY",
        "TEST_DISABLE_BACKGROUND_SERVICES",
        "DISABLE_TELEMETRY",
        "Jwt__Enabled",
        "Jwt__Key",
        "Jwt__Issuer",
        "Jwt__Audience",
        "ForwardedHeaders__Enabled",
        "ForwardedHeaders__KnownProxies__0",
        "ForwardedHeaders__KnownProxies__1",
        "ForwardedHeaders__ForwardLimit",
    ];

    [Fact]
    public async Task Hosts_WhenStartedConcurrently_UseIsolatedConfigurationWithoutEnvironmentMutation()
    {
        Dictionary<string, string?> before = SnapshotFactoryEnvironment();
        CustomWebApplicationFactory firstFactory = new();
        CustomWebApplicationFactory secondFactory = new();
        using Barrier startBarrier = new(2);

        try
        {
            Task<HostIdentity> firstHost =
                StartHostAndCaptureIdentityAsync(firstFactory, startBarrier);
            Task<HostIdentity> secondHost =
                StartHostAndCaptureIdentityAsync(secondFactory, startBarrier);

            HostIdentity[] identities = await Task.WhenAll(firstHost, secondHost);

            AssertHostIdentity(firstFactory, identities[0]);
            AssertHostIdentity(secondFactory, identities[1]);
            Assert.NotEqual(firstFactory.ConnectionString, secondFactory.ConnectionString);
            Assert.NotEqual(
                identities[0].AppDatabaseConnectionString,
                identities[1].AppDatabaseConnectionString);
            Assert.NotEqual(
                identities[0].SlicerDatabaseConnectionString,
                identities[1].SlicerDatabaseConnectionString);
        }
        finally
        {
            await firstFactory.DisposeAsync();
            await secondFactory.DisposeAsync();
            AssertFactoryEnvironmentUnchanged(before);
        }
    }

    [Fact]
    public void Services_WhenHostStarts_BindsTrustedProxyConfigurationWithoutEnvironmentMutation()
    {
        Dictionary<string, string?> before = SnapshotFactoryEnvironment();
        using CustomWebApplicationFactory factory = new();

        ForwardedHeadersSettings settings =
            factory.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
        ForwardedHeadersOptions options =
            factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(settings.Enabled);
        Assert.Equal(1, settings.ForwardLimit);
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, options.KnownProxies);
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        AssertFactoryEnvironmentUnchanged(before);
    }

    private static Task<HostIdentity> StartHostAndCaptureIdentityAsync(
        CustomWebApplicationFactory factory,
        Barrier startBarrier)
        => Task.Run(async () =>
        {
            Assert.True(startBarrier.SignalAndWait(TimeSpan.FromSeconds(30)));

            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            IConfiguration configuration =
                factory.Services.GetRequiredService<IConfiguration>();
            IHostEnvironment environment =
                factory.Services.GetRequiredService<IHostEnvironment>();
            using IServiceScope scope = factory.Services.CreateScope();
            AppDbContext appDb =
                scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SlicerDbContext slicerDb =
                scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

            return new HostIdentity(
                environment.EnvironmentName,
                configuration.GetConnectionString("Default"),
                configuration.GetConnectionString("Sqlite"),
                configuration.GetConnectionString("DefaultConnection"),
                configuration.GetConnectionString("SlicerDatabase"),
                appDb.Database.GetConnectionString(),
                slicerDb.Database.GetConnectionString(),
                configuration["TEST_USE_SQLITE_INMEMORY"],
                configuration["TEST_DISABLE_BACKGROUND_SERVICES"],
                configuration["DISABLE_TELEMETRY"],
                configuration["Jwt:Enabled"],
                configuration["Jwt:Key"],
                configuration["Jwt:Issuer"],
                configuration["Jwt:Audience"]);
        });

    private static void AssertHostIdentity(
        CustomWebApplicationFactory factory,
        HostIdentity identity)
    {
        Assert.Equal("Testing", identity.EnvironmentName);
        Assert.Equal(factory.ConnectionString, identity.DefaultConnectionString);
        Assert.Equal(factory.ConnectionString, identity.SqliteConnectionString);
        Assert.Equal(factory.ConnectionString, identity.DefaultConnectionAlias);
        Assert.Equal(factory.ConnectionString, identity.SlicerDatabaseAlias);
        Assert.Equal(factory.ConnectionString, identity.AppDatabaseConnectionString);
        Assert.Equal(factory.ConnectionString, identity.SlicerDatabaseConnectionString);
        Assert.Equal("true", identity.UseInMemorySqlite);
        Assert.Equal("true", identity.DisableBackgroundServices);
        Assert.Equal("true", identity.DisableTelemetry);
        Assert.Equal("true", identity.JwtEnabled);
        Assert.Equal("test-integration-key-please-change-0123456789", identity.JwtKey);
        Assert.Equal("PrintFarmer", identity.JwtIssuer);
        Assert.Equal("PrintFarmer", identity.JwtAudience);
    }

    private static Dictionary<string, string?> SnapshotFactoryEnvironment()
        => FactoryEnvironmentVariables.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);

    private static void AssertFactoryEnvironmentUnchanged(
        IReadOnlyDictionary<string, string?> expected)
    {
        foreach (string key in FactoryEnvironmentVariables)
        {
            Assert.Equal(expected[key], Environment.GetEnvironmentVariable(key));
        }
    }

    private sealed record HostIdentity(
        string EnvironmentName,
        string? DefaultConnectionString,
        string? SqliteConnectionString,
        string? DefaultConnectionAlias,
        string? SlicerDatabaseAlias,
        string? AppDatabaseConnectionString,
        string? SlicerDatabaseConnectionString,
        string? UseInMemorySqlite,
        string? DisableBackgroundServices,
        string? DisableTelemetry,
        string? JwtEnabled,
        string? JwtKey,
        string? JwtIssuer,
        string? JwtAudience);
}
