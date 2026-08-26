using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Modules.Abstractions.Tests;

/// <summary>
/// A minimal <see cref="IApiModule"/> used only to exercise discovery/registration. Must be
/// public with a parameterless constructor so <see cref="ApiModuleHostExtensions.AddApiModules"/>
/// can find and instantiate it via reflection over loaded assemblies.
/// </summary>
public sealed class TestApiModule : IApiModule
{
    public string Name => "Test";

    public bool ConfigureServicesCalled { get; private set; }

    public bool MapEndpointsCalled { get; private set; }

    public IConfiguration? ObservedConfiguration { get; private set; }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ConfigureServicesCalled = true;
        ObservedConfiguration = configuration;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        MapEndpointsCalled = true;
    }
}

public sealed class ApiModuleHostExtensionsTests
{
    [Fact]
    public void AddApiModules_DiscoversAndRegistersModuleFromLoadedAssembly()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IMvcBuilder mvcBuilder = builder.Services.AddControllers();

        builder.Services.AddApiModules(mvcBuilder, builder.Configuration);

        using WebApplication app = builder.Build();

        IApiModule[] modules = [.. app.Services.GetServices<IApiModule>()];
        TestApiModule? discovered = modules.OfType<TestApiModule>().SingleOrDefault();

        discovered.Should().NotBeNull();
        discovered!.ConfigureServicesCalled.Should().BeTrue();
        discovered.ObservedConfiguration.Should().BeSameAs(builder.Configuration);

        ApplicationPartManager partManager = app.Services.GetRequiredService<ApplicationPartManager>();
        partManager.ApplicationParts.Should().Contain(
            p => p.Name == typeof(TestApiModule).Assembly.GetName().Name);
    }

    [Fact]
    public void MapApiModules_InvokesMapEndpointsOnEveryDiscoveredModule()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IMvcBuilder mvcBuilder = builder.Services.AddControllers();
        builder.Services.AddApiModules(mvcBuilder, builder.Configuration);

        using WebApplication app = builder.Build();
        app.MapApiModules();

        TestApiModule discovered = app.Services.GetServices<IApiModule>().OfType<TestApiModule>().Single();
        discovered.MapEndpointsCalled.Should().BeTrue();
    }

    [Fact]
    public void AddApiModules_NullArguments_Throw()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IMvcBuilder mvcBuilder = builder.Services.AddControllers();

        Action nullServices = () => ((Microsoft.Extensions.DependencyInjection.IServiceCollection)null!)
            .AddApiModules(mvcBuilder, builder.Configuration);
        Action nullMvcBuilder = () => builder.Services.AddApiModules(null!, builder.Configuration);
        Action nullConfiguration = () => builder.Services.AddApiModules(mvcBuilder, null!);

        nullServices.Should().Throw<ArgumentNullException>();
        nullMvcBuilder.Should().Throw<ArgumentNullException>();
        nullConfiguration.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MapApiModules_NullEndpoints_Throws()
    {
        Action act = () => ((IEndpointRouteBuilder)null!).MapApiModules();

        act.Should().Throw<ArgumentNullException>();
    }
}

public sealed class IdempotentAttributeTests
{
    private sealed class FakeIdempotencyFilter : Farm.Modules.Abstractions.Idempotency.IIdempotencyFilter
    {
    }

    [Fact]
    public void Constructor_BlankRouteKey_Throws()
    {
        Action act = () => new Farm.Modules.Abstractions.Idempotency.IdempotentAttribute(" ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_SetsRouteKeyAndDefaultOrder()
    {
        var attribute = new Farm.Modules.Abstractions.Idempotency.IdempotentAttribute("parts-inventory:adjust");

        attribute.RouteKey.Should().Be("parts-inventory:adjust");
        attribute.Order.Should().Be(-500);
        attribute.IsReusable.Should().BeFalse();
    }

    [Fact]
    public void CreateInstance_ResolvesRegisteredFilterFromServiceProvider()
    {
        var attribute = new Farm.Modules.Abstractions.Idempotency.IdempotentAttribute("route");
        ServiceCollection services = new();
        FakeIdempotencyFilter filter = new();
        services.AddSingleton<Farm.Modules.Abstractions.Idempotency.IIdempotencyFilter>(filter);
        using ServiceProvider provider = services.BuildServiceProvider();

        IFilterMetadata resolved = attribute.CreateInstance(provider);

        resolved.Should().BeSameAs(filter);
    }

    [Fact]
    public void CreateInstance_NullServiceProvider_Throws()
    {
        var attribute = new Farm.Modules.Abstractions.Idempotency.IdempotentAttribute("route");

        Action act = () => attribute.CreateInstance(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
