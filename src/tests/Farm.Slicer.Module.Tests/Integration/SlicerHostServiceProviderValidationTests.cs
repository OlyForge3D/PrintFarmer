extern alias SlicerHost;

using System;
using System.IO;
using Farm.Infrastructure.Data;
using Farm.Slicer.Module.Data;
using Farm.Slicers.OrcaSlicer.v2_4_0;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using SlicerHostProgram = SlicerHost::Program;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Regression tests for issue #1232: <c>Farm.Slicer.Host</c> crash-looped at startup because
/// <see cref="IDbContextFactory{AppDbContext}"/> was registered <c>Singleton</c> while
/// <see cref="DbContextOptions{AppDbContext}"/> (from <c>AddDbContext&lt;AppDbContext&gt;</c>)
/// stayed <c>Scoped</c>. ASP.NET Core's DI scope validator refused to build the provider and
/// the container exited before any HTTP surface was reachable.
/// </summary>
/// <remarks>
/// <para>
/// The existing <c>StandaloneSlicerHostApplicationFactory</c>-based tests hosted the same
/// <c>Program.cs</c> under environment <c>Testing</c>, which turns
/// <c>ServiceProviderOptions.ValidateOnBuild</c> and <c>ValidateScopes</c> off (both default
/// to <c>true</c> only in <c>Development</c> when the host is built via
/// <c>WebApplication.CreateBuilder</c>; in <c>Production</c> they both default to
/// <c>false</c>). That is why the lifetime bug slipped past CI — it is only observable when
/// scope validation is on, which is exactly what runs inside the slicer-host container when
/// operators use PrintFarmer's default deployment. The container image's
/// <c>Dockerfile</c> stages set <c>ASPNETCORE_ENVIRONMENT=Production</c>, but
/// <c>deploy-docker.sh</c> writes <c>ASPNETCORE_ENVIRONMENT=Development</c> into the
/// generated <c>.env</c> file (default answer to the interactive environment prompt), and
/// <c>docker-compose.slicer-host.yml</c> overrides the image default from that <c>.env</c>.
/// </para>
/// <para>
/// This test forces <c>ValidateOnBuild = true</c> and <c>ValidateScopes = true</c> so any
/// future regression in shared-infrastructure lifetimes fails a targeted xUnit test rather
/// than a deploy.
/// </para>
/// </remarks>
public sealed class SlicerHostServiceProviderValidationTests
{
    [Fact]
    public void SlicerHost_StartsWithValidateOnBuildAndValidateScopes()
    {
        using ValidatingSlicerHostFactory factory = new();

        // Materialising Services triggers WebApplicationBuilder.Build() and
        // ServiceProvider construction. With ValidateOnBuild=true this throws
        // AggregateException on any invalid lifetime graph before the first request.
        IServiceProvider services = factory.Services;

        services.Should().NotBeNull("the WebApplication must build cleanly with scope validation on");
    }

    [Fact]
    public void SlicerHost_ResolvesAppDbContextFactoryFromScopedProvider()
    {
        using ValidatingSlicerHostFactory factory = new();

        // AppDbContext-backed services (settings, catalog, etc.) resolve
        // IDbContextFactory<AppDbContext> per request from a request scope.
        // Before the fix this graph was itself invalid — the singleton
        // IDbContextFactory<AppDbContext> captured a scoped
        // DbContextOptions<AppDbContext>, so ValidateOnBuild never even got
        // as far as letting anything ask for the factory from a scope.
        using IServiceScope scope = factory.Services.CreateScope();

        IDbContextFactory<AppDbContext> appFactory =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        IDbContextFactory<SlicerDbContext> slicerFactory =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<SlicerDbContext>>();

        appFactory.Should().NotBeNull();
        slicerFactory.Should().NotBeNull();
    }

    /// <summary>
    /// Hosts <c>Farm.Slicer.Host</c>'s real <c>Program.cs</c> under a minimal in-memory SQLite
    /// database with <c>ServiceProviderOptions.ValidateOnBuild</c> and
    /// <c>ValidateScopes</c> forced on. That combination reproduces the deployment-time DI
    /// validation used by the slicer-host container regardless of the ambient
    /// <c>ASPNETCORE_ENVIRONMENT</c>.
    /// </summary>
    private sealed class ValidatingSlicerHostFactory : WebApplicationFactory<SlicerHostProgram>
    {
        private const string JwtKey = "SlicerHostServiceProviderValidationTestsKey-1234567890abcdef";

        private readonly string _testRoot;
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public ValidatingSlicerHostFactory()
        {
            // A file-based SQLite DB would work, but a shared-cache in-memory DB
            // keeps the test hermetic and avoids leaving artefacts behind.
            _testRoot = Path.Combine(
                Path.GetTempPath(),
                $"slicer-host-di-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testRoot);

            string dbName = $"slicer-host-di-{Guid.NewGuid():N}";
            _connectionString = $"Data Source=file:{dbName}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            // Force the OrcaSlicer plugin assembly to load so
            // Program.cs' post-build "at least one slicer library" sanity
            // check succeeds when Services is materialised.
            _ = typeof(OrcaSlicerLibrary_v2_4_0).Assembly.GetName();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Deliberately do NOT UseEnvironment("Testing") here: the JWT
            // handler in Program.cs relaxes HTTPS metadata only for "Testing",
            // but scope validation is what this fixture exists to test and
            // Testing turns validation off. We instead force validation on
            // via UseDefaultServiceProvider.
            _ = builder.UseEnvironment("Development");

            _ = builder.UseSetting("Jwt:Key", JwtKey);
            _ = builder.UseSetting("Jwt:Issuer", "PrintFarmer");
            _ = builder.UseSetting("Jwt:Audience", "PrintFarmer");
            _ = builder.UseSetting("DB_PROVIDER", "sqlite");
            _ = builder.UseSetting("ConnectionStrings:Default", _connectionString);
            _ = builder.UseSetting("STORAGE_PATHS:UPLOADS", Path.Combine(_testRoot, "models"));
            _ = builder.UseSetting("STORAGE_PATHS:GCODE", Path.Combine(_testRoot, "gcode"));
            _ = builder.UseSetting("WorkerAuth:SharedKey", "slicer-host-di-shared-key");
            _ = builder.UseSetting(
                "ArtifactStorage:RootPath",
                Path.Combine(_testRoot, "artifacts"));
            _ = builder.UseSetting("ArtifactStorage:EnableStorageAlerts", "false");

            // The whole point of this fixture: reproduce the deploy-time
            // container's strict DI validation. If any registration graph is
            // invalid (e.g. Singleton → Scoped), ServiceProvider construction
            // throws AggregateException here and every test in this class fails.
            _ = builder.UseDefaultServiceProvider((_, options) =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _keepAlive.Dispose();
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(_testRoot))
                    {
                        Directory.Delete(_testRoot, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort cleanup; SQLite files may still be locked
                    // briefly on Windows CI runners.
                }
            }

            base.Dispose(disposing);
        }
    }
}
