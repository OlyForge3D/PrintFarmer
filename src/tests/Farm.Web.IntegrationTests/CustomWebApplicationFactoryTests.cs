using System.Net;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Network;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
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
        "WorkerAuth__SharedKey",
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
            Assert.NotEqual(identities[0].JwtSigningKey, identities[1].JwtSigningKey);
            Assert.NotEqual(identities[0].JwtIssuer, identities[1].JwtIssuer);
            Assert.NotEqual(identities[0].JwtAudience, identities[1].JwtAudience);
            Assert.NotEqual(identities[0].WorkerSharedKey, identities[1].WorkerSharedKey);
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

    [Fact]
    public async Task Database_WhenContextConnectionsCloseAndReopen_PersistsWithinFactoryAndIsolatesFactories()
    {
        CustomWebApplicationFactory firstFactory = new();
        CustomWebApplicationFactory secondFactory = new();
        const string probeValue = "first-factory";

        try
        {
            await WriteLifetimeProbeAsync(firstFactory, probeValue);

            Assert.Equal(probeValue, await ReadLifetimeProbeAsync(firstFactory));
            Assert.False(await LifetimeProbeTableExistsAsync(secondFactory));
        }
        finally
        {
            await firstFactory.DisposeAsync();
            await secondFactory.DisposeAsync();
        }

        await using SqliteConnection reopenedAfterDisposal = new(firstFactory.ConnectionString);
        await reopenedAfterDisposal.OpenAsync();
        Assert.False(await LifetimeProbeTableExistsAsync(reopenedAfterDisposal));
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
            CustomWebApplicationFactory.StartupRegistrationSnapshot startupSnapshot =
                factory.Services.GetRequiredService<CustomWebApplicationFactory.StartupRegistrationSnapshot>();
            JwtBearerOptions jwtOptions =
                factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                    .Get(JwtBearerDefaults.AuthenticationScheme);
            SymmetricSecurityKey jwtSigningKey =
                Assert.IsType<SymmetricSecurityKey>(
                    jwtOptions.TokenValidationParameters.IssuerSigningKey);
            Type[] hostedServiceTypes = factory.Services
                .GetServices<IHostedService>()
                .Select(service => service.GetType())
                .ToArray();

            return new HostIdentity(
                environment.EnvironmentName,
                startupSnapshot,
                configuration.GetConnectionString("Default"),
                configuration.GetConnectionString("Sqlite"),
                configuration.GetConnectionString("DefaultConnection"),
                configuration.GetConnectionString("SlicerDatabase"),
                appDb.Database.GetConnectionString(),
                slicerDb.Database.GetConnectionString(),
                appDb.Database.ProviderName,
                slicerDb.Database.ProviderName,
                configuration["TEST_USE_SQLITE_INMEMORY"],
                configuration["TEST_DISABLE_BACKGROUND_SERVICES"],
                configuration["DISABLE_TELEMETRY"],
                configuration["Jwt:Enabled"],
                Encoding.UTF8.GetString(jwtSigningKey.Key),
                jwtOptions.TokenValidationParameters.ValidIssuer,
                jwtOptions.TokenValidationParameters.ValidAudience,
                configuration["WorkerAuth:SharedKey"],
                jwtOptions.RequireHttpsMetadata,
                factory.Services.GetService<TracerProvider>() is not null,
                factory.Services.GetService<MeterProvider>() is not null,
                hostedServiceTypes);
        });

    private static void AssertHostIdentity(
        CustomWebApplicationFactory factory,
        HostIdentity identity)
    {
        Assert.Equal("Testing", identity.EnvironmentName);
        Assert.Equal("Testing", identity.StartupSnapshot.EnvironmentName);
        Assert.False(identity.StartupSnapshot.TelemetryRegistered);
        Assert.False(identity.StartupSnapshot.GuardedBackgroundServicesRegistered);
        Assert.Equal(factory.ConnectionString, identity.DefaultConnectionString);
        Assert.Equal(factory.ConnectionString, identity.SqliteConnectionString);
        Assert.Equal(factory.ConnectionString, identity.DefaultConnectionAlias);
        Assert.Equal(factory.ConnectionString, identity.SlicerDatabaseAlias);
        Assert.Equal(factory.ConnectionString, identity.AppDatabaseConnectionString);
        Assert.Equal(factory.ConnectionString, identity.SlicerDatabaseConnectionString);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", identity.AppDatabaseProvider);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", identity.SlicerDatabaseProvider);
        Assert.Equal("true", identity.UseInMemorySqlite);
        Assert.Equal("true", identity.DisableBackgroundServices);
        Assert.Equal("true", identity.DisableTelemetry);
        Assert.Equal("true", identity.JwtEnabled);
        Assert.Equal(factory.JwtSigningKey, identity.JwtSigningKey);
        Assert.Equal(factory.JwtIssuer, identity.JwtIssuer);
        Assert.Equal(factory.JwtAudience, identity.JwtAudience);
        Assert.Equal(factory.WorkerSharedKey, identity.WorkerSharedKey);
        Assert.False(identity.JwtRequireHttpsMetadata);
        Assert.False(identity.TracerProviderRegistered);
        Assert.False(identity.MeterProviderRegistered);

        Assert.DoesNotContain(
            typeof(Farm.Infrastructure.Services.GcodeHarvest.GcodeHarvestQueueProcessorService),
            identity.HostedServiceTypes);
        Assert.DoesNotContain(
            typeof(Farm.Infrastructure.Services.SystemLogs.SystemLogCleanupService),
            identity.HostedServiceTypes);
        Assert.DoesNotContain(
            typeof(Farm.Modules.Administration.Services.Workers.DiscoveryHeartbeatMonitorService),
            identity.HostedServiceTypes);
        Assert.DoesNotContain(
            typeof(Farm.Infrastructure.Services.Queue.Dispatch.AutoDispatchBackgroundService),
            identity.HostedServiceTypes);
        Assert.DoesNotContain(
            typeof(Farm.Infrastructure.Services.Cameras.CameraHealthMonitorService),
            identity.HostedServiceTypes);
        Assert.DoesNotContain(
            typeof(Farm.Infrastructure.Services.FailureDetection.PrintFailureMonitorService),
            identity.HostedServiceTypes);
    }

    private static async Task WriteLifetimeProbeAsync(
        CustomWebApplicationFactory factory,
        string value)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();

        try
        {
            _ = await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE FactoryLifetimeProbe (Value TEXT NOT NULL)");
            _ = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO FactoryLifetimeProbe (Value) VALUES ({value})");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> ReadLifetimeProbeAsync(
        CustomWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();

        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Value FROM FactoryLifetimeProbe";
            return await command.ExecuteScalarAsync() as string;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> LifetimeProbeTableExistsAsync(
        CustomWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync();

        try
        {
            return await LifetimeProbeTableExistsAsync(
                Assert.IsType<SqliteConnection>(db.Database.GetDbConnection()));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> LifetimeProbeTableExistsAsync(
        SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'FactoryLifetimeProbe'";
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static Dictionary<string, string?> SnapshotFactoryEnvironment()
        => FactoryEnvironmentVariables.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);

    private static void AssertFactoryEnvironmentUnchanged(
        Dictionary<string, string?> expected)
    {
        foreach (string key in FactoryEnvironmentVariables)
        {
            Assert.Equal(expected[key], Environment.GetEnvironmentVariable(key));
        }
    }

    private sealed record HostIdentity(
        string EnvironmentName,
        CustomWebApplicationFactory.StartupRegistrationSnapshot StartupSnapshot,
        string? DefaultConnectionString,
        string? SqliteConnectionString,
        string? DefaultConnectionAlias,
        string? SlicerDatabaseAlias,
        string? AppDatabaseConnectionString,
        string? SlicerDatabaseConnectionString,
        string? AppDatabaseProvider,
        string? SlicerDatabaseProvider,
        string? UseInMemorySqlite,
        string? DisableBackgroundServices,
        string? DisableTelemetry,
        string? JwtEnabled,
        string? JwtSigningKey,
        string? JwtIssuer,
        string? JwtAudience,
        string? WorkerSharedKey,
        bool JwtRequireHttpsMetadata,
        bool TracerProviderRegistered,
        bool MeterProviderRegistered,
        Type[] HostedServiceTypes);
}
