extern alias SlicerHost;

using SlicerHost::Farm.Slicer.Host;
using SlicerHost::Farm.Slicer.Host.Services;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        builder.Configuration["DB_PROVIDER"] = "sqlite";
        builder.Configuration["ConnectionStrings:Default"] = "Data Source=:memory:";

        _ = builder.Services.AddSlicerModule(builder.Configuration);
        _ = builder.Services.AddSlicerApiServices(builder.Configuration);
        _ = builder.Services.AddCrossDomainLookupServices(builder.Configuration);
        _ = builder.Services.AddSharedInfrastructureServices(builder.Configuration);
        _ = builder.Services.AddUnimplementedSlicerServiceStubs();
        _ = builder.Services.AddSignalR();

        using ServiceProvider provider = builder.Services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        using IServiceScope scope = provider.CreateScope();
        Assert.NotNull(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<Farm.Infrastructure.Data.AppDbContext>>());
    }
}
