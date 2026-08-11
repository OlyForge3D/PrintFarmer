using System;
using System.Net;
using Farm.Infrastructure.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IPNetwork = System.Net.IPNetwork;

namespace Farm.Web.Api.Infrastructure;

/// <summary>
/// Helpers for wiring the ASP.NET Core forwarded-headers middleware from the
/// <see cref="ForwardedHeadersSettings"/> configuration section.
///
/// Security: only apply <c>UseForwardedHeaders</c> when
/// <see cref="ForwardedHeadersSettings.Enabled"/> is <c>true</c>, so an
/// operator that never opts in cannot have <c>X-Forwarded-For</c> silently
/// spoof the connection IP (regression protection for issue #862).
/// </summary>
public static class ForwardedHeadersConfiguration
{
    /// <summary>
    /// Binds <see cref="ForwardedHeadersSettings"/> and, when enabled,
    /// configures <see cref="ForwardedHeadersOptions"/> so that only
    /// operator-declared proxies are trusted. When disabled this is a no-op
    /// and no forwarding headers are honored.
    /// </summary>
    public static IServiceCollection AddPrintFarmerForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(ForwardedHeadersSettings.SectionName);
        services.Configure<ForwardedHeadersSettings>(section);

        ForwardedHeadersSettings settings = section.Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        if (!settings.Enabled)
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = Math.Max(1, settings.ForwardLimit);

            // Framework defaults trust loopback (127.0.0.1 / ::1) implicitly.
            // Clear so operators must opt in every trusted proxy explicitly.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (string proxy in settings.KnownProxies)
            {
                if (IPAddress.TryParse(proxy, out IPAddress? address) && address != null)
                {
                    options.KnownProxies.Add(address);
                }
            }

            foreach (string network in settings.KnownNetworks)
            {
                if (IPNetwork.TryParse(network, out IPNetwork parsed))
                {
                    options.KnownIPNetworks.Add(parsed);
                }
            }
        });

        return services;
    }

    /// <summary>
    /// Invokes <see cref="ForwardedHeadersExtensions.UseForwardedHeaders(IApplicationBuilder)"/>
    /// only when <see cref="ForwardedHeadersSettings.Enabled"/> is <c>true</c>.
    /// Must run early in the pipeline — before authentication, rate limiting,
    /// or any middleware that inspects <c>Connection.RemoteIpAddress</c>.
    /// </summary>
    public static IApplicationBuilder UsePrintFarmerForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        IConfiguration configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        ForwardedHeadersSettings settings =
            configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>()
            ?? new ForwardedHeadersSettings();

        if (!settings.Enabled)
        {
            return app;
        }

        ILoggerFactory loggerFactory = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = loggerFactory.CreateLogger("Farm.Infrastructure.Network.ForwardedHeaders");

        LogSkippedEntries(logger, settings);

        app.UseForwardedHeaders();
        logger.LogInformation(
            "Forwarded headers enabled. Trusted proxies={ProxyCount}, trusted networks={NetworkCount}, forwardLimit={ForwardLimit}",
            settings.KnownProxies.Count,
            settings.KnownNetworks.Count,
            settings.ForwardLimit);

        if (settings.KnownProxies.Count == 0 && settings.KnownNetworks.Count == 0)
        {
            logger.LogWarning(
                "ForwardedHeaders is enabled but no KnownProxies or KnownNetworks are configured. " +
                "The framework default (loopback only) has been cleared, so X-Forwarded-For will be ignored for all connections. " +
                "Add the reverse proxy's IP or CIDR to ForwardedHeaders:KnownProxies / KnownNetworks.");
        }

        return app;
    }

    private static void LogSkippedEntries(ILogger logger, ForwardedHeadersSettings settings)
    {
        foreach (string proxy in settings.KnownProxies.Where(proxy => !IPAddress.TryParse(proxy, out _)))
        {
            logger.LogWarning("ForwardedHeaders:KnownProxies entry '{Entry}' is not a valid IP address and was skipped.", proxy);
        }

        foreach (string network in settings.KnownNetworks.Where(network => !IPNetwork.TryParse(network, out _)))
        {
            logger.LogWarning("ForwardedHeaders:KnownNetworks entry '{Entry}' is not a valid CIDR range and was skipped.", network);
        }
    }
}
