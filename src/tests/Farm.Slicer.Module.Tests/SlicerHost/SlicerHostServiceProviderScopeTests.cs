extern alias SlicerHost;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlicerHost::Farm.Slicer.Host;
using SlicerHost::Farm.Slicer.Host.Services;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerHost;

public sealed class SlicerHostServiceProviderScopeTests
{
    [Fact]
    public void SlicerHostServiceProvider_WithScopeValidation_BuildsSuccessfully()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });

        builder.Configuration["DEPLOYMENT_MODE"] = string.Empty;
        builder.Configuration["Slicer:PluginsPath"] = string.Empty;
        builder.Configuration["DB_PROVIDER"] = "sqlite";
        builder.Configuration["ConnectionStrings:Default"] = "Data Source=:memory:";

        _ = builder.Services.AddSlicerModule(builder.Configuration);
        _ = builder.Services.AddSlicerApiServices(builder.Configuration);
        _ = builder.Services.AddCrossDomainLookupServices(builder.Configuration);
        _ = builder.Services.AddSharedInfrastructureServices(builder.Configuration);
        _ = builder.Services.AddUnimplementedSlicerServiceStubs();
        _ = builder.Services.AddSignalR();

        builder.Host.UseDefaultServiceProvider((_, options) =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        using WebApplication app = builder.Build();
        using IServiceScope scope = app.Services.CreateScope();
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<Farm.Infrastructure.Data.AppDbContext>>());
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<SlicerDbContext>>());

        // Confirms the #1469 DI chain (IAuthAuditLogRepository -> EfAuthAuditLogRepository,
        // IAuthAuditService -> AuthAuditService, ITokenRevocationService -> TokenRevocationService
        // wrapped by CachingTokenRevocationService) resolves under ValidateOnBuild/ValidateScopes,
        // so a broken registration fails the build instead of silently no-oping like #1460 did.
        Assert.NotNull(
            scope.ServiceProvider
                .GetRequiredService<Farm.Infrastructure.Services.Authentication.ITokenRevocationService>());

        // Confirms #1544: ProfileTaskCheckService resolves its printer data source
        // (IPrinterProfileCheckRepository) via GetRequiredService at runtime inside a scope,
        // which ValidateOnBuild alone would NOT catch because the hosted service's constructor
        // only takes IServiceScopeFactory/ILogger/IConfiguration. Resolving it directly here
        // exercises the actual runtime dependency chain and would have failed before the fix,
        // when ProfileTaskCheckService instead required the unregistered main-API IPrintersService.
        Assert.NotNull(
            scope.ServiceProvider
                .GetRequiredService<Farm.Slicer.Module.Api.Repositories.IPrinterProfileCheckRepository>());
    }
}
